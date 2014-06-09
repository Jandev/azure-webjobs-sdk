﻿using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Blob;
using Newtonsoft.Json;

#if PUBLICPROTOCOL
namespace Microsoft.Azure.Jobs.Protocols
#else
namespace Microsoft.Azure.Jobs.Host.Protocols
#endif
{
    /// <summary>Represents a persistent queue.</summary>
    /// <typeparam name="T">The type of messages in the queue.</typeparam>
#if PUBLICPROTOCOL
    [CLSCompliant(false)]
    public class PersistentQueue<T> : IPersistentQueue<T> where T : PersistentQueueMessage
#else
    internal class PersistentQueue<T> : IPersistentQueue<T> where T : PersistentQueueMessage
#endif
    {
        private const string NextVisibleTimeKey = "NextVisibleTime";
        private const string CreatedKey = "Created";

        private readonly CloudBlobContainer _blobContainer;

        /// <summary>Initializes a new instance of the <see cref="PersistentQueue{T}"/> class.</summary>
        /// <param name="client">
        /// A blob client for the storage account into which host output messages are written.
        /// </param>
        public PersistentQueue(CloudBlobClient client)
            : this(client.GetContainerReference(ContainerNames.HostOutputContainerName))
        {
        }

        /// <summary>Initializes a new instance of the <see cref="PersistentQueue{T}"/> class.</summary>
        /// <param name="container">The container into which host output messages are written.</param>
        public PersistentQueue(CloudBlobContainer container)
        {
            _blobContainer = container;
        }

        /// <inheritdoc />
        public T Dequeue()
        {
            ICloudBlob possibleNextItem;
            T nextItem = null;

            // Keep racing to take ownership of the next visible item until that succeeds or there are no more left.
            do
            {
                possibleNextItem = GetNextVisibleItem();

                // There two reasons to keep racing:
                // 1. We tried to mark the item as invisible, and failed (409, someone else won the race)
                // 2. We then tried to download the item and failed (404, someone else finished processing an item, even
                // though we owned it).
            } while (possibleNextItem != null && (!TryMakeItemInvisible(possibleNextItem)
                || !TryDownloadItem(possibleNextItem, out nextItem)));

            return nextItem;
        }

        private ICloudBlob GetNextVisibleItem()
        {
            BlobContinuationToken currentToken = null;
            BlobResultSegment segment;
            ICloudBlob nextVisibleItem = null;

            do
            {
                segment = GetSegment(currentToken);

                if (segment == null)
                {
                    return null;
                }

                currentToken = segment.ContinuationToken;
                nextVisibleItem = GetNextVisibleItem(segment.Results);
            } while (nextVisibleItem == null && currentToken != null);

            return nextVisibleItem;
        }

        private BlobResultSegment GetSegment(BlobContinuationToken currentToken)
        {
            const int batchSize = 100;

            try
            {
                return _blobContainer.ListBlobsSegmented(prefix: null,
                    useFlatBlobListing: true,
                    blobListingDetails: BlobListingDetails.Metadata,
                    maxResults: batchSize,
                    currentToken: currentToken,
                    options: null,
                    operationContext: null);
            }
            catch (StorageException exception)
            {
                if (exception.IsNotFound())
                {
                    return null;
                }
                else
                {
                    throw;
                }
            }
        }

        private ICloudBlob GetNextVisibleItem(IEnumerable<IListBlobItem> results)
        {
            if (results == null)
            {
                return null;
            }

            return results.OfType<ICloudBlob>().FirstOrDefault(b => !b.Metadata.ContainsKey(NextVisibleTimeKey)
                || IsInPast(b.Metadata[NextVisibleTimeKey]));
        }

        private static bool IsInPast(string nextVisibleTimeValue)
        {
            DateTimeOffset nextVisibleTime;

            if (!DateTimeOffset.TryParseExact(nextVisibleTimeValue, "o", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out nextVisibleTime))
            {
                // Treat invalid next visible time values as already expired.
                return true;
            }

            return DateTimeOffset.UtcNow > nextVisibleTime;
        }

        private static bool TryMakeItemInvisible(ICloudBlob item)
        {
            // After this window expires, others may attempt to process the item.
            const double processingWindowInMinutes = 5;

            item.Metadata[NextVisibleTimeKey] =
                DateTimeOffset.UtcNow.AddMinutes(processingWindowInMinutes).ToString("o", CultureInfo.InvariantCulture);

            if (!item.Metadata.ContainsKey(CreatedKey))
            {
                item.Metadata.Add(CreatedKey, DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture));
            }

            try
            {
                item.SetMetadata(new AccessCondition { IfMatchETag = item.Properties.ETag });
                return true;
            }
            catch (StorageException exception)
            {
                if (exception.IsPreconditionFailed())
                {
                    return false;
                }
                else
                {
                    throw;
                }
            }
        }

        private static bool TryDownloadItem(ICloudBlob possibleNextItem, out T nextItem)
        {
            string contents;

            try
            {
                using (Stream stream = possibleNextItem.OpenRead())
                {
                    using (TextReader reader = new StreamReader(stream))
                    {
                        contents = reader.ReadToEnd();
                    }
                }
            }
            catch (StorageException exception)
            {
                if (exception.IsNotFound())
                {
                    // If the item no longer exists, someone else finished processing it, and we should look for another
                    // next item.
                    nextItem = null;
                    return false;
                }
                else
                {
                    throw;
                }
            }

            nextItem = JsonConvert.DeserializeObject<T>(contents, JsonSerialization.Settings);
            nextItem.EnqueuedOn = GetCreatedOn(possibleNextItem);
            nextItem.PopReceipt = possibleNextItem.Name;
            return true;
        }

        private static DateTimeOffset GetCreatedOn(ICloudBlob item)
        {
            string createdOnValue;

            if (item.Metadata.ContainsKey(CreatedKey))
            {
                createdOnValue = item.Metadata[CreatedKey];
            }
            else
            {
                // This should never happen, since we always add this metadata when taking ownership of the blob.
                createdOnValue = null;
            }

            DateTimeOffset createdOn;

            if (createdOnValue != null && DateTimeOffset.TryParseExact(createdOnValue, "o",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out createdOn))
            {
                return createdOn;
            }

            return item.Properties.LastModified.GetValueOrDefault(DateTimeOffset.UtcNow);
        }

        /// <inheritdoc />
        public void Enqueue(T message)
        {
            _blobContainer.CreateIfNotExists();

            Guid messageId = Guid.NewGuid();
            CloudBlockBlob blob = _blobContainer.GetBlockBlobReference(messageId.ToString("N"));
            string messageBody = JsonConvert.SerializeObject(message, JsonSerialization.Settings);
            blob.UploadText(messageBody);
        }

        /// <inheritdoc />
        public void Delete(T message)
        {
            ICloudBlob blob = _blobContainer.GetBlockBlobReference(message.PopReceipt);
            blob.DeleteIfExists();
        }
    }
}