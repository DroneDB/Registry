﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Registry.Web.Models;
using Registry.Web.Models.DTO;

namespace Registry.Web.Services.Ports
{
    public interface IObjectsManager
    {
        Task<IEnumerable<ObjectDto>> List(string orgSlug, string dsSlug, string path = null, bool recursive = false);
        Task<ObjectRes> Get(string orgSlug, string dsSlug, string path);
        Task<UploadedObjectDto> AddNew(string orgSlug, string dsSlug, string path, byte[] data);
        Task<UploadedObjectDto> AddNew(string orgSlug, string dsSlug, string path, Stream stream);
        Task Delete(string orgSlug, string dsSlug, string path);

        Task DeleteAll(string orgSlug, string dsSlug);

        Task<FileDescriptorDto> Download(string orgSlug, string dsSlug, string[] paths);
        Task<FileDescriptor> DownloadStream(string orgSlug, string dsSlug, string[] paths);
        Task<string> GetDownloadPackage(string orgSlug, string dsSlug, string[] paths, DateTime? expiration = null, bool isPublic = false);
        Task<FileDescriptorDto> DownloadPackage(string orgSlug, string dsSlug, string packageId);
        Task<FileDescriptorDto> GenerateThumbnail(string orgSlug, string dsSlug, string path, int? size, bool recreate = false);
        Task<FileDescriptorDto> GenerateTile(string orgSlug, string dsSlug, string path, int tz, int tx, int ty, bool retina);
        string GetBucketName(string orgSlug, Guid internalRef);
        Task<FileDescriptorDto> GetDdb(string orgSlug, string dsSlug);
        Task EnsureBucketExists(string bucketName);
    }
}
