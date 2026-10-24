using System;
using System.Collections.Generic;
using Microsoft.Extensions.Configuration;
using Registry.Web.Models.Configuration;
using Registry.Web.Models.DTO;
using Registry.Web.Utilities;
using static Registry.Web.Services.Configuration.ConfigurationFields;

namespace Registry.Web.Services.Configuration;

/// <summary>The admin configuration sections of uploads, downloads and exports.</summary>
public sealed class TransferSections : IConfigurationSectionBuilder
{
    private readonly IConfigurationView _view;

    public TransferSections(IConfigurationView view)
    {
        _view = view;
    }

    public IEnumerable<PlacedSection> Build() =>
    [
        new(ConfigurationSection.Upload, BuildUploadSection()),
        new(ConfigurationSection.Downloads, BuildDownloadsSection()),
        new(ConfigurationSection.ExportZip, BuildExportZipSection())
    ];

    private ConfigurationSectionDataDto BuildUploadSection()
    {
        var http = _view.Configured<HttpHostOptions>();
        var httpDefault = _view.Shipped<HttpHostOptions>();
        var share = _view.Configured<ShareOptions>();
        var shareDefault = _view.Shipped<ShareOptions>();
        var uploads = _view.Configured<UploadSettings>();
        var uploadsDefault = _view.Shipped<UploadSettings>();
        return Section("Upload", "Upload Settings",
            "Request body size limits, batch upload timeouts, and token configuration.",
            [
                NullableNumberField("MaxRequestBodySize", "Max Request Body Size",
                    "Maximum HTTP request body size in bytes. Null means unlimited.",
                    http.MaxRequestBodySize, httpDefault.MaxRequestBodySize,
                    unit: "bytes"),

                TimeSpanField("UploadBatchTimeout", "Upload Batch Timeout",
                    "Maximum duration for a batch upload session before it expires.",
                    share.UploadBatchTimeout, shareDefault.UploadBatchTimeout),

                NumberField("BatchTokenLength", "Batch Token Length",
                    "Length of randomly generated batch upload tokens. Must be at least 8.",
                    share.BatchTokenLength, shareDefault.BatchTokenLength,
                    minValue: 8),

                NumberField("Uploads.MaxPartsPerRequest", "Max Files per Upload Request",
                    "Most files the Hub may bundle in one upload request (small files are bundled to save round trips).",
                    uploads.MaxPartsPerRequest, uploadsDefault.MaxPartsPerRequest, minValue: 1),

                NumberField("Uploads.MaxChunkBytes", "Max Upload Chunk Size",
                    "Largest chunk accepted by the resumable upload of large files.",
                    uploads.MaxChunkBytes, uploadsDefault.MaxChunkBytes, minValue: 1048576, unit: "bytes"),

                NumberField("Uploads.QuotaWindowSeconds", "Upload Quota Evaluation Window",
                    "How long one evaluation of a user's storage use serves their uploads (their sizes are added to it).",
                    uploads.QuotaWindowSeconds, uploadsDefault.QuotaWindowSeconds, minValue: 0, unit: "s"),

                NumberField("Uploads.InvalidationWindowMs", "Upload Invalidation Window",
                    "How long the OGC and dataset-thumbnail invalidations of uploaded files are collected before they run once.",
                    uploads.InvalidationWindowMs, uploadsDefault.InvalidationWindowMs, minValue: 0, unit: "ms"),

                NumberField("Uploads.ThumbnailPrecomputeWorkers", "Thumbnail Pre-generation Workers",
                    "Background workers that generate the thumbnails of uploaded images ahead of their first view.",
                    uploads.ThumbnailPrecomputeWorkers, uploadsDefault.ThumbnailPrecomputeWorkers, minValue: 1)
            ]);
    }

    private ConfigurationSectionDataDto BuildDownloadsSection()
    {
        var downloads = _view.Configured<DownloadOptions>();
        var downloadsDefault = _view.Shipped<DownloadOptions>();
        return Section("Downloads", "Download Settings",
            "Concurrent download limits and anonymous bulk download restrictions.",
            [
                NullableNumberField("MaxConcurrentDownloadsPerUser", "Max Concurrent Downloads",
                    "Maximum simultaneous downloads per user. Null = unlimited.",
                    downloads.MaxConcurrentDownloadsPerUser, downloadsDefault.MaxConcurrentDownloadsPerUser,
                    minValue: 1),

                BoolField("DisableAnonymousBulkDownloads", "Disable Anonymous Bulk Downloads",
                    "When true, anonymous users cannot download bulk archives (whole dataset, folders, multi-file). Single-file downloads remain allowed.",
                    downloads.DisableAnonymousBulkDownloads, downloadsDefault.DisableAnonymousBulkDownloads)
            ]);
    }

    private ConfigurationSectionDataDto BuildExportZipSection()
    {
        var downloads = _view.Configured<DownloadOptions>();
        var downloadsDefault = _view.Shipped<DownloadOptions>();
        return Section("ExportZip", "Export & Zip",
            "Maximum GeoTIFF export sizes. ZIP downloads are streamed and need no memory threshold.",
            [
                NullableNumberField("MaxExportSizeBytes", "Max Export Size",
                    "Maximum estimated output size for GeoTIFF raster export. Null = unlimited.",
                    downloads.MaxExportSizeBytes, downloadsDefault.MaxExportSizeBytes,
                    unit: "bytes")
            ]);
    }
}
