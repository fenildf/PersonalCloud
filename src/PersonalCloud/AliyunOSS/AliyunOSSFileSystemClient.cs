using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Aliyun.OSS;

using NSPersonalCloud.Interfaces.FileSystem;

namespace NSPersonalCloud.FileSharing.Aliyun
{
    public class AliyunOSSFileSystemClient : IFileSystem
    {
        private readonly OssConfig _OssConfig;

        public AliyunOSSFileSystemClient(OssConfig config)
        {
            _OssConfig = config;
        }

        #region IReadableFileSystem

        public ValueTask<List<FileSystemEntry>> EnumerateChildrenAsync(string filePath, CancellationToken cancellation = default)
        {
            filePath = filePath?.Replace("\\", "/", StringComparison.InvariantCulture).Trim('/') ?? "";
            if (filePath.Length > 0)
            {
                filePath += "/";
            }
            var client = new OssClient(_OssConfig.OssEndpoint, _OssConfig.AccessKeyId, _OssConfig.AccessKeySecret);
            var items = client.ListObjects(new ListObjectsRequest(_OssConfig.BucketName) {
                Prefix = filePath,
                Delimiter = "/"
            });

            var files = new List<FileSystemEntry>();
            foreach (var item in items.CommonPrefixes)
            {
                files.Add(new FileSystemEntry {
                    Attributes = FileAttributes.Normal | FileAttributes.Directory,
                    Size = 0,
                    Name = item.Substring(filePath.Length).TrimEnd('/')
                });
            }

            foreach (var item in items.ObjectSummaries)
            {
                if (item.Key.EndsWith("/", StringComparison.InvariantCulture))
                {
                    string sFolderName = item.Key.Substring(filePath.Length).TrimEnd('/');
                    if (!string.IsNullOrEmpty(sFolderName))
                    {
                        files.Add(new FileSystemEntry {
                            Attributes = FileAttributes.Normal | FileAttributes.Directory,
                            ModificationDate = item.LastModified,
                            Size = 0,
                            Name = sFolderName
                        });
                    }
                }
                else
                {
                    string sFileName = item.Key.Substring(filePath.Length);
                    files.Add(new FileSystemEntry {
                        Attributes = FileAttributes.Normal,
                        ModificationDate = item.LastModified,
                        Size = item.Size,
                        Name = sFileName
                    });
                }
            }

            return new ValueTask<List<FileSystemEntry>>(files);
        }

        public ValueTask<List<FileSystemEntry>> EnumerateChildrenAsync(string path, string searchPattern, int pageSize, int pageIndex, CancellationToken cancellation = default)
        {
            throw new NotImplementedException();
        }

        public ValueTask<FreeSpaceInformation> GetFreeSpaceAsync(CancellationToken cancellation = default)
        {
            throw new NotImplementedException();
        }

        public ValueTask<FileSystemEntry> ReadMetadataAsync(string filePath, CancellationToken cancellation = default)
        {
            ObjectMetadata fileMetadata = null;

            filePath = filePath?.Replace("\\", "/", StringComparison.InvariantCulture).TrimStart('/');
            if (string.IsNullOrEmpty(filePath))
            {
                // Root Folder
                return new ValueTask<FileSystemEntry>(new FileSystemEntry {
                    Attributes = FileAttributes.Normal | FileAttributes.Directory,
                    Size = 0,
                    Name = string.Empty
                });
            }
            var client = new OssClient(_OssConfig.OssEndpoint, _OssConfig.AccessKeyId, _OssConfig.AccessKeySecret);

            if (filePath.EndsWith("/", StringComparison.InvariantCulture))
            {
                if (_IsDirectory(filePath, client))
                {
                    return new ValueTask<FileSystemEntry>(new FileSystemEntry {
                        Attributes = FileAttributes.Normal | FileAttributes.Directory,
                        Size = 0,
                        Name = GetRelativeName(filePath)
                    });
                }
            }
            else
            {
                try
                {
                    fileMetadata = client.GetObjectMetadata(_OssConfig.BucketName, filePath);
                    return new ValueTask<FileSystemEntry>(new FileSystemEntry {
                        Attributes = FileAttributes.Normal,
                        ModificationDate = fileMetadata.LastModified,
                        Size = fileMetadata.ContentLength,
                        Name = GetRelativeName(filePath)
                    });
                }
                catch
                {
                    // No such file
                }

                if (fileMetadata == null)
                {
                    if (_IsDirectory(filePath, client))
                    {
                        return new ValueTask<FileSystemEntry>(new FileSystemEntry {
                            Attributes = FileAttributes.Normal | FileAttributes.Directory,
                            Size = 0,
                            Name = GetRelativeName(filePath)
                        });
                    }
                }
            }

            return default;
        }

        private bool _IsDirectory(string filePath, OssClient client)
        {
            if (!filePath.EndsWith("/", StringComparison.InvariantCulture)) filePath += "/";

            var result = client.ListObjects(new ListObjectsRequest(_OssConfig.BucketName) {
                Prefix = filePath,
                Delimiter = "/",
                MaxKeys = 1
            });

            return (result?.CommonPrefixes?.Count() > 0 || result?.ObjectSummaries?.Count() > 0);
        }

        private string GetRelativeName(string path)
        {
            path = path?.Replace("\\", "/", StringComparison.InvariantCulture).Trim('/');
            if (!string.IsNullOrEmpty(path))
            {
                int nPos = path.LastIndexOf('/');
                if (nPos >= 0)
                {
                    return path.Substring(nPos + 1);
                }
                else
                {
                    return path;
                }
            }
            else
            {
                return string.Empty;
            }
        }

        public ValueTask<Stream> ReadFileAsync(string fileName, CancellationToken cancellation = default)
        {
            fileName = fileName?.Replace("\\", "/", StringComparison.InvariantCulture).Trim('/');
            var client = new OssClient(_OssConfig.OssEndpoint, _OssConfig.AccessKeyId, _OssConfig.AccessKeySecret);
            var req = new GetObjectRequest(_OssConfig.BucketName, fileName);
            var fileObj = client.GetObject(req);
            return new ValueTask<Stream>(fileObj.Content);
        }

        public ValueTask<Stream> ReadPartialFileAsync(string path, long fromPosition, long includeToPosition, CancellationToken cancellation = default)
        {
            throw new NotImplementedException();
        }

        #endregion IReadableFileSystem

        #region IWritableFileSystem

        public ValueTask CreateDirectoryAsync(string path, CancellationToken cancellation = default)
        {
            throw new NotImplementedException();
        }

        public ValueTask WriteFileAsync(string path, Stream data, CancellationToken cancellation = default)
        {
            throw new NotImplementedException();
        }

        public ValueTask WritePartialFileAsync(string path, long offset, long dataLength, Stream data, CancellationToken cancellation = default)
        {
            throw new NotImplementedException();
        }

        public ValueTask RenameAsync(string path, string name, CancellationToken cancellation = default)
        {
            throw new NotImplementedException();
        }

        public ValueTask DeleteAsync(string path, bool safeDelete = false, CancellationToken cancellation = default)
        {
            throw new NotImplementedException();
        }

        public ValueTask SetFileLengthAsync(string path, long length, CancellationToken cancellation = default)
        {
            throw new NotImplementedException();
        }

        public ValueTask SetFileAttributesAsync(string path, FileAttributes attributes, CancellationToken cancellation = default)
        {
            throw new NotImplementedException();
        }

        public ValueTask SetFileTimeAsync(string path, DateTime? creationTime, DateTime? lastAccessTime, DateTime? lastWriteTime, CancellationToken cancellation = default)
        {
            throw new NotImplementedException();
        }

        #endregion IWritableFileSystem
    }
}