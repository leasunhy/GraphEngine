﻿using System;
using System.Threading.Tasks;
using System.Linq;
using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Blob;
using Trinity.DynamicCluster.Persistency;
using System.Threading;
using Trinity.Diagnostics;
using LogLevel = Trinity.Diagnostics.LogLevel;

namespace Trinity.Azure.Storage
{
    public class BlobStoragePersistentStorage : IPersistentStorage
    {
        private CloudStorageAccount m_storageAccount;
        private CloudBlobClient m_client;
        private CloudBlobContainer m_container;
        private CancellationTokenSource m_cancellationTokenSource;
        private CancellationToken m_cancel;

        internal CloudBlobClient _test_getclient() => m_client;

        public BlobStoragePersistentStorage()
        {
            if(BlobStorageConfig.Instance.ContainerName == null) Log.WriteLine(LogLevel.Error, $"{nameof(BlobStoragePersistentStorage)}: container name is not specified");
            if(BlobStorageConfig.Instance.ConnectionString == null) Log.WriteLine(LogLevel.Error, $"{nameof(BlobStoragePersistentStorage)}: connection string is not specified");
            if(BlobStorageConfig.Instance.ContainerName != BlobStorageConfig.Instance.ContainerName.ToLower()) Log.WriteLine(LogLevel.Error, $"{nameof(BlobStoragePersistentStorage)}: invalid container name");
            Log.WriteLine(LogLevel.Debug, $"{nameof(BlobStoragePersistentStorage)}: Initializing.");
            m_storageAccount = CloudStorageAccount.Parse(BlobStorageConfig.Instance.ConnectionString);
            m_client = m_storageAccount.CreateCloudBlobClient();
            m_container = m_client.GetContainerReference(BlobStorageConfig.Instance.ContainerName);
            m_cancellationTokenSource = new CancellationTokenSource();
            m_cancel = m_cancellationTokenSource.Token;
        }

        private async Task EnsureContainer()
        {
            await m_container.CreateIfNotExistsAsync(cancellationToken: m_cancel);
        }

        public async Task<Guid> CreateNewVersion()
        {
            await EnsureContainer();
retry:
            var guid = Guid.NewGuid();
            var dir  = m_container.GetDirectoryReference(guid.ToString());
            if (dir.ListBlobs(useFlatBlobListing: true).Any()) goto retry;
            try
            {
                var blob = dir.GetBlockBlobReference(Constants.c_uploading);
                await blob.UploadFromByteArrayAsync(new byte[1], 0, 1, cancellationToken: m_cancel);
            }
            catch
            {
                // cleanup
                await DeleteVersion(guid);
                throw;
            }
            Log.WriteLine(LogLevel.Info, $"{nameof(BlobStoragePersistentStorage)}: Created new version {guid}.");
            return guid;
        }

        public async Task DeleteVersion(Guid version)
        {
            await EnsureContainer();
            var blobs = m_container.GetDirectoryReference(version.ToString())
                       .ListBlobs(useFlatBlobListing:true)
                       .OfType<CloudBlob>()
                       .Select(_ => _.DeleteIfExistsAsync(cancellationToken:m_cancel));
            await Task.WhenAll(blobs);
            Log.WriteLine(LogLevel.Info, $"{nameof(BlobStoragePersistentStorage)}: Version {version} deleted.");
        }

        public void Dispose()
        {
            m_cancellationTokenSource.Cancel();
            m_cancellationTokenSource.Dispose();
        }

        public async Task<Guid> GetLatestVersion()
        {
            await EnsureContainer();
            var files = m_container.ListBlobs(useFlatBlobListing: false)
                                      .OfType<CloudBlobDirectory>()
                                      .Select(dir => dir.GetBlockBlobReference(Constants.c_finished))
                                      .ToDictionary(f => f, f => f.ExistsAsync(m_cancel));
            await Task.WhenAll(files.Values.ToArray());
            var latest = files.Where(kvp => kvp.Value.Result)
                              .Select(kvp => kvp.Key)
                              .OrderByDescending(f => f.Properties.LastModified.Value)
                              .FirstOrDefault();
            if (latest == null) throw new NoDataException();
            var guid = new Guid(latest.Parent.Uri.Segments.Last().TrimEnd('/'));
            Log.WriteLine(LogLevel.Info, $"{nameof(BlobStoragePersistentStorage)}: {guid} is the latest version.");
            return guid;
        }

        public Task<PersistentStorageMode> QueryPersistentStorageMode()
        {
            return Task.FromResult(PersistentStorageMode.AC_FileSystem | PersistentStorageMode.LO_External | PersistentStorageMode.ST_MechanicalDrive);
        }

        public async Task<IPersistentUploader> Upload(Guid version, long lowKey, long highKey)
        {
            await EnsureContainer();
            return new BlobUploader(version, lowKey, highKey, m_container);
        }

        public async Task<IPersistentDownloader> Download(Guid version, long lowKey, long highKey)
        {
            await EnsureContainer();
            return new BlobDownloader(version, lowKey, highKey, m_container);
        }

    }
}
