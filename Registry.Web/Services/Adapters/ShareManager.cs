﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Registry.Common;
using Registry.Web.Data;
using Registry.Web.Data.Models;
using Registry.Web.Exceptions;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.Adapters
{
    public class ShareManager : IShareManager
    {

        private readonly IOrganizationsManager _organizationsManager;
        private readonly IDatasetsManager _datasetsManager;
        private readonly IObjectsManager _objectsManager;
        private readonly ILogger<ShareManager> _logger;
        private readonly IUtils _utils;
        private readonly IAuthManager _authManager;
        private readonly RegistryContext _context;

        private const int TokenLength = 32;

        // TODO: Implement queue
        public ShareManager(
            ILogger<ShareManager> logger,
            IObjectsManager objectsManager,
            IDatasetsManager datasetsManager,
            IOrganizationsManager organizationsManager,
            IUtils utils,
            IAuthManager authManager,
            RegistryContext context)
        {
            _logger = logger;
            _objectsManager = objectsManager;
            _datasetsManager = datasetsManager;
            _organizationsManager = organizationsManager;
            _utils = utils;
            _authManager = authManager;
            _context = context;
        }

        public async Task<IEnumerable<BatchDto>> ListBatches(string orgSlug, string dsSlug)
        {
            await _utils.GetOrganizationAndCheck(orgSlug);
            var dataset = await _utils.GetDatasetAndCheck(orgSlug, dsSlug);

            _logger.LogInformation($"Listing batches of '{orgSlug}/{dsSlug}'");

            var batches = from batch in _context.Batches
                    .Include(x => x.Entries)
                    .Include(x => x.Dataset)
                          where batch.Dataset.Id == dataset.Id
                          select new BatchDto
                          {
                              End = batch.End,
                              Start = batch.Start,
                              Token = batch.Token,
                              UserName = batch.UserName,
                              Entries = from entry in batch.Entries
                                        select new EntryDto
                                        {
                                            Hash = entry.Hash,
                                            Type = entry.Type,
                                            Size = entry.Size,
                                            AddedOn = entry.AddedOn,
                                            Path = entry.Path
                                        }
                          };


            return batches;
        }

        public async Task<string> Initialize(ShareInitDto parameters)
        {
            if (parameters == null)
                throw new BadRequestException("Invalid parameters");

            TagDto tag;

            try
            {
                tag = parameters.Tag.ToTag();
            }
            catch (FormatException ex)
            {
                throw new BadRequestException($"Invalid tag: {ex.Message}");
            }

            // Let's fill the names if not provided
            parameters.DatasetName ??= tag.DatasetSlug;

            var org = await _utils.GetOrganizationAndCheck(tag.OrganizationSlug, true);

            // Org must exist
            if (org == null)
            {
                throw new BadRequestException($"Organization '{tag.OrganizationSlug}' does not exist");
            }

            _logger.LogInformation("Organization found");

            // Create dataset if not exists
            var dataset = await _utils.GetDatasetAndCheck(tag.OrganizationSlug, tag.DatasetSlug, true);

            if (dataset == null)
            {
                _logger.LogInformation($"Dataset '{tag.DatasetSlug}' not found, creating it");

                await _datasetsManager.AddNew(tag.DatasetSlug, new DatasetDto
                {
                    Slug = tag.DatasetSlug,
                    Name = parameters.DatasetName,
                    Description = parameters.DatasetDescription
                });
                dataset = await _utils.GetDatasetAndCheck(tag.OrganizationSlug, tag.DatasetSlug);

                _logger.LogInformation("Dataset created");
            }
            else
            {
                _logger.LogInformation("Dataset and organization already existing, checking for running batches");

                if (dataset.Batches.Any(item => item.End == null))
                {
                    _logger.LogInformation("Found already running batches, cannot start a new one");
                    throw new BadRequestException("Cannot start a new batch if there are others already running");
                }
            }

            var batch = new Batch
            {
                Dataset = dataset,
                Start = DateTime.Now,
                Token = CommonUtils.RandomString(TokenLength),
                UserName = await _authManager.SafeGetCurrentUserName()
            };

            _logger.LogInformation($"Adding new batch for user '{batch.UserName}' with token '{batch.Token}'");

            await _context.Batches.AddAsync(batch);
            await _context.SaveChangesAsync();

            _logger.LogInformation("Batch created, it is now possible to upload files");

            return batch.Token;
        }


        public async Task Upload(string token, string path, byte[] data)
        {
            if (string.IsNullOrWhiteSpace(token))
                throw new BadRequestException("Missing token");

            if (string.IsNullOrWhiteSpace(path))
                throw new BadRequestException("Missing path");

            if (data == null || data.Length == 0)
                throw new BadRequestException("Missing data");

            var batch = _context.Batches
                .Include(x => x.Dataset.Organization)
                .Include(x => x.Entries)
                .FirstOrDefault(item => item.Token == token);

            if (batch == null)
                throw new NotFoundException("Cannot find batch");

            if (batch.End != null)
                throw new BadRequestException("Cannot upload file to closed batch");

            var currentUserName = await _authManager.SafeGetCurrentUserName();

            if (!(await _authManager.IsUserAdmin() || batch.UserName == currentUserName))
                throw new BadRequestException("This batch does not belong to you");

            var entry = batch.Entries.FirstOrDefault(item => item.Path == path);

            if (entry != null)
                throw new BadRequestException("Entry already uploaded");

            _logger.LogInformation($"Adding '{path}' in batch '{batch.Token}'");

            var orgSlug = batch.Dataset.Organization.Slug;
            var dsSlug = batch.Dataset.Slug;

            await _objectsManager.AddNew(orgSlug, dsSlug, path, data);

            var info = (await _objectsManager.List(orgSlug, dsSlug, path)).FirstOrDefault();

            if (info == null)
                throw new BadRequestException("Underlying object storage is not working correctly: cannot find object after adding it");

            entry = new Entry
            {
                Type = (EntryType)(int)info.Type,
                Hash = info.Hash,
                AddedOn = DateTime.Now,
                Path = path,
                Size = info.Size,
                Batch = batch
            };

            await _context.AddAsync(entry);

            _logger.LogInformation("Entry added");

            await _context.SaveChangesAsync();

            _logger.LogInformation("Changes commited");

        }

        public async Task Commit(string token)
        {

            if (string.IsNullOrWhiteSpace(token))
                throw new BadRequestException("Missing token");

            var batch = _context.Batches.FirstOrDefault(item => item.Token == token);

            if (batch == null)
                throw new NotFoundException("Cannot find batch");

            if (batch.End != null)
                throw new BadRequestException("Cannot commit a closed batch");

            var currentUserName = await _authManager.SafeGetCurrentUserName();

            if (!(await _authManager.IsUserAdmin() || batch.UserName == currentUserName))
                throw new BadRequestException("This batch does not belong to you");

            batch.End = DateTime.Now;

            _logger.LogInformation($"Committing batch '{token}' @ {batch.End.Value.ToLongDateString()} {batch.End.Value.ToLongTimeString()}");

            // TODO: Commit?

            await _context.SaveChangesAsync();

        }
    }
}