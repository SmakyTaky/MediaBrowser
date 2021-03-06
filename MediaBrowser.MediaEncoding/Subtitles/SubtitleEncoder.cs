﻿using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Extensions;
using MediaBrowser.Common.IO;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.MediaInfo;
using MediaBrowser.Model.Serialization;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UniversalDetector;

namespace MediaBrowser.MediaEncoding.Subtitles
{
    public class SubtitleEncoder : ISubtitleEncoder
    {
        private readonly ILibraryManager _libraryManager;
        private readonly ILogger _logger;
        private readonly IApplicationPaths _appPaths;
        private readonly IFileSystem _fileSystem;
        private readonly IMediaEncoder _mediaEncoder;
        private readonly IJsonSerializer _json;

        public SubtitleEncoder(ILibraryManager libraryManager, ILogger logger, IApplicationPaths appPaths, IFileSystem fileSystem, IMediaEncoder mediaEncoder, IJsonSerializer json)
        {
            _libraryManager = libraryManager;
            _logger = logger;
            _appPaths = appPaths;
            _fileSystem = fileSystem;
            _mediaEncoder = mediaEncoder;
            _json = json;
        }

        private string SubtitleCachePath
        {
            get
            {
                return Path.Combine(_appPaths.CachePath, "subtitles");
            }
        }

        public async Task<Stream> ConvertSubtitles(Stream stream,
            string inputFormat,
            string outputFormat,
            long startTimeTicks,
            long? endTimeTicks,
            CancellationToken cancellationToken)
        {
            var ms = new MemoryStream();

            try
            {
                var trackInfo = await GetTrackInfo(stream, inputFormat, cancellationToken).ConfigureAwait(false);

                FilterEvents(trackInfo, startTimeTicks, endTimeTicks, false);

                var writer = GetWriter(outputFormat);

                writer.Write(trackInfo, ms, cancellationToken);
                ms.Position = 0;
            }
            catch
            {
                ms.Dispose();
                throw;
            }

            return ms;
        }

        private void FilterEvents(SubtitleTrackInfo track, long startPositionTicks, long? endTimeTicks, bool preserveTimestamps)
        {
            // Drop subs that are earlier than what we're looking for
            track.TrackEvents = track.TrackEvents
                .SkipWhile(i => (i.StartPositionTicks - startPositionTicks) < 0 || (i.EndPositionTicks - startPositionTicks) < 0)
                .ToList();

            if (endTimeTicks.HasValue)
            {
                var endTime = endTimeTicks.Value;

                track.TrackEvents = track.TrackEvents
                    .TakeWhile(i => i.StartPositionTicks <= endTime)
                    .ToList();
            }

            if (!preserveTimestamps)
            {
                foreach (var trackEvent in track.TrackEvents)
                {
                    trackEvent.EndPositionTicks -= startPositionTicks;
                    trackEvent.StartPositionTicks -= startPositionTicks;
                }
            }
        }

        public async Task<Stream> GetSubtitles(string itemId,
            string mediaSourceId,
            int subtitleStreamIndex,
            string outputFormat,
            long startTimeTicks,
            long? endTimeTicks,
            CancellationToken cancellationToken)
        {
            var subtitle = await GetSubtitleStream(itemId, mediaSourceId, subtitleStreamIndex, cancellationToken)
                        .ConfigureAwait(false);

            using (var stream = subtitle.Item1)
            {
                var inputFormat = subtitle.Item2;

                return await ConvertSubtitles(stream, inputFormat, outputFormat, startTimeTicks, endTimeTicks, cancellationToken).ConfigureAwait(false);
            }
        }

        private async Task<Tuple<Stream, string>> GetSubtitleStream(string itemId,
            string mediaSourceId,
            int subtitleStreamIndex,
            CancellationToken cancellationToken)
        {
            var item = (Video)_libraryManager.GetItemById(new Guid(itemId));

            var mediaSource = item.GetMediaSources(false)
                .First(i => string.Equals(i.Id, mediaSourceId));

            var subtitleStream = mediaSource.MediaStreams
                .First(i => i.Type == MediaStreamType.Subtitle && i.Index == subtitleStreamIndex);

            var inputFiles = new[] { mediaSource.Path };

            if (mediaSource.VideoType.HasValue)
            {
                if (mediaSource.VideoType.Value == VideoType.BluRay ||
                    mediaSource.VideoType.Value == VideoType.Dvd)
                {
                    var mediaSourceItem = (Video)_libraryManager.GetItemById(new Guid(mediaSourceId));
                    inputFiles = mediaSourceItem.GetPlayableStreamFiles().ToArray();
                }
            }

            var fileInfo = await GetReadableFile(mediaSource.Path, inputFiles, mediaSource.Protocol, subtitleStream, cancellationToken).ConfigureAwait(false);

            var stream = await GetSubtitleStream(fileInfo.Item1, fileInfo.Item3).ConfigureAwait(false);

            return new Tuple<Stream, string>(stream, fileInfo.Item2);
        }

        private async Task<Stream> GetSubtitleStream(string path, bool requiresCharset)
        {
            if (requiresCharset)
            {
                var charset = GetSubtitleFileCharacterSet(path);

                if (!string.IsNullOrEmpty(charset))
                {
                    using (var fs = _fileSystem.GetFileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, true))
                    {
                        using (var reader = new StreamReader(fs, GetEncoding(charset)))
                        {
                            var text = await reader.ReadToEndAsync().ConfigureAwait(false);

                            var bytes = Encoding.UTF8.GetBytes(text);

                            return new MemoryStream(bytes);
                        }
                    }
                }
            }

            return File.OpenRead(path);
        }

        private Encoding GetEncoding(string charset)
        {
            if (string.IsNullOrWhiteSpace(charset))
            {
                throw new ArgumentNullException("charset");
            }

            try
            {
                return Encoding.GetEncoding(charset);
            }
            catch (ArgumentException)
            {
                return Encoding.GetEncoding(charset.Replace("-", string.Empty));
            }
        }

        private async Task<Tuple<string, string, bool>> GetReadableFile(string mediaPath,
            string[] inputFiles,
            MediaProtocol protocol,
            MediaStream subtitleStream,
            CancellationToken cancellationToken)
        {
            if (!subtitleStream.IsExternal)
            {
                string outputFormat;
                string outputCodec;

                if (string.Equals(subtitleStream.Codec, "ass", StringComparison.OrdinalIgnoreCase))
                {
                    // Extract    
                    outputCodec = "copy";
                    outputFormat = "ass";
                }
                else if (string.Equals(subtitleStream.Codec, "subrip", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(subtitleStream.Codec, "srt", StringComparison.OrdinalIgnoreCase))
                {
                    // Extract    
                    outputCodec = "copy";
                    outputFormat = "srt";
                }
                else
                {
                    // Extract    
                    outputCodec = "srt";
                    outputFormat = "srt";
                }

                // Extract    
                var outputPath = GetSubtitleCachePath(mediaPath, subtitleStream.Index, "." + outputFormat);

                await ExtractTextSubtitle(inputFiles, protocol, subtitleStream.Index, outputCodec, outputPath, cancellationToken)
                        .ConfigureAwait(false);

                return new Tuple<string, string, bool>(outputPath, outputFormat, false);
            }

            var currentFormat = (Path.GetExtension(subtitleStream.Path) ?? subtitleStream.Codec)
                .TrimStart('.');

            if (GetReader(currentFormat, false) == null)
            {
                // Convert    
                var outputPath = GetSubtitleCachePath(mediaPath, subtitleStream.Index, ".srt");

                await ConvertTextSubtitleToSrt(subtitleStream.Path, outputPath, cancellationToken).ConfigureAwait(false);

                return new Tuple<string, string, bool>(outputPath, "srt", true);
            }

            return new Tuple<string, string, bool>(subtitleStream.Path, currentFormat, true);
        }

        private async Task<SubtitleTrackInfo> GetTrackInfo(Stream stream,
            string inputFormat,
            CancellationToken cancellationToken)
        {
            var reader = GetReader(inputFormat, true);

            return reader.Parse(stream, cancellationToken);
        }

        private ISubtitleParser GetReader(string format, bool throwIfMissing)
        {
            if (string.IsNullOrEmpty(format))
            {
                throw new ArgumentNullException("format");
            }

            if (string.Equals(format, SubtitleFormat.SRT, StringComparison.OrdinalIgnoreCase))
            {
                return new SrtParser(_logger);
            }
            if (string.Equals(format, SubtitleFormat.SSA, StringComparison.OrdinalIgnoreCase))
            {
                return new SsaParser();
            }
            if (string.Equals(format, SubtitleFormat.ASS, StringComparison.OrdinalIgnoreCase))
            {
                return new AssParser();
            }

            if (throwIfMissing)
            {
                throw new ArgumentException("Unsupported format: " + format);
            }

            return null;
        }

        private ISubtitleWriter GetWriter(string format)
        {
            if (string.IsNullOrEmpty(format))
            {
                throw new ArgumentNullException("format");
            }

            if (string.Equals(format, "json", StringComparison.OrdinalIgnoreCase))
            {
                return new JsonWriter(_json);
            }
            if (string.Equals(format, SubtitleFormat.SRT, StringComparison.OrdinalIgnoreCase))
            {
                return new SrtWriter();
            }
            if (string.Equals(format, SubtitleFormat.VTT, StringComparison.OrdinalIgnoreCase))
            {
                return new VttWriter();
            }
            if (string.Equals(format, SubtitleFormat.TTML, StringComparison.OrdinalIgnoreCase))
            {
                return new TtmlWriter();
            }

            throw new ArgumentException("Unsupported format: " + format);
        }

        /// <summary>
        /// The _semaphoreLocks
        /// </summary>
        private readonly ConcurrentDictionary<string, SemaphoreSlim> _semaphoreLocks =
            new ConcurrentDictionary<string, SemaphoreSlim>();

        /// <summary>
        /// Gets the lock.
        /// </summary>
        /// <param name="filename">The filename.</param>
        /// <returns>System.Object.</returns>
        private SemaphoreSlim GetLock(string filename)
        {
            return _semaphoreLocks.GetOrAdd(filename, key => new SemaphoreSlim(1, 1));
        }

        /// <summary>
        /// Converts the text subtitle to SRT.
        /// </summary>
        /// <param name="inputPath">The input path.</param>
        /// <param name="outputPath">The output path.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>Task.</returns>
        public async Task ConvertTextSubtitleToSrt(string inputPath, string outputPath, CancellationToken cancellationToken)
        {
            var semaphore = GetLock(outputPath);

            await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                if (!File.Exists(outputPath))
                {
                    await ConvertTextSubtitleToSrtInternal(inputPath, outputPath).ConfigureAwait(false);
                }
            }
            finally
            {
                semaphore.Release();
            }
        }

        /// <summary>
        /// Converts the text subtitle to SRT internal.
        /// </summary>
        /// <param name="inputPath">The input path.</param>
        /// <param name="outputPath">The output path.</param>
        /// <returns>Task.</returns>
        /// <exception cref="System.ArgumentNullException">inputPath
        /// or
        /// outputPath</exception>
        /// <exception cref="System.ApplicationException"></exception>
        private async Task ConvertTextSubtitleToSrtInternal(string inputPath, string outputPath)
        {
            if (string.IsNullOrEmpty(inputPath))
            {
                throw new ArgumentNullException("inputPath");
            }

            if (string.IsNullOrEmpty(outputPath))
            {
                throw new ArgumentNullException("outputPath");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(outputPath));

            var encodingParam = GetSubtitleFileCharacterSet(inputPath);

            if (!string.IsNullOrEmpty(encodingParam))
            {
                encodingParam = " -sub_charenc " + encodingParam;
            }

            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    RedirectStandardOutput = false,
                    RedirectStandardError = true,
                    RedirectStandardInput = true,

                    CreateNoWindow = true,
                    UseShellExecute = false,
                    FileName = _mediaEncoder.EncoderPath,
                    Arguments = string.Format("{0} -i \"{1}\" -c:s srt \"{2}\"", encodingParam, inputPath, outputPath),

                    WindowStyle = ProcessWindowStyle.Hidden,
                    ErrorDialog = false
                }
            };

            _logger.Debug("{0} {1}", process.StartInfo.FileName, process.StartInfo.Arguments);

            var logFilePath = Path.Combine(_appPaths.LogDirectoryPath, "ffmpeg-sub-convert-" + Guid.NewGuid() + ".txt");
            Directory.CreateDirectory(Path.GetDirectoryName(logFilePath));

            var logFileStream = _fileSystem.GetFileStream(logFilePath, FileMode.Create, FileAccess.Write, FileShare.Read,
                true);

            try
            {
                process.Start();
            }
            catch (Exception ex)
            {
                logFileStream.Dispose();

                _logger.ErrorException("Error starting ffmpeg", ex);

                throw;
            }

            var logTask = process.StandardError.BaseStream.CopyToAsync(logFileStream);

            var ranToCompletion = process.WaitForExit(60000);

            if (!ranToCompletion)
            {
                try
                {
                    _logger.Info("Killing ffmpeg subtitle conversion process");

                    process.StandardInput.WriteLine("q");
                    process.WaitForExit(1000);

                    await logTask.ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.ErrorException("Error killing subtitle conversion process", ex);
                }
                finally
                {
                    logFileStream.Dispose();
                }
            }

            var exitCode = ranToCompletion ? process.ExitCode : -1;

            process.Dispose();

            var failed = false;

            if (exitCode == -1)
            {
                failed = true;

                if (File.Exists(outputPath))
                {
                    try
                    {
                        _logger.Info("Deleting converted subtitle due to failure: ", outputPath);
                        _fileSystem.DeleteFile(outputPath);
                    }
                    catch (IOException ex)
                    {
                        _logger.ErrorException("Error deleting converted subtitle {0}", ex, outputPath);
                    }
                }
            }
            else if (!File.Exists(outputPath))
            {
                failed = true;
            }

            if (failed)
            {
                var msg = string.Format("ffmpeg subtitle converted failed for {0}", inputPath);

                _logger.Error(msg);

                throw new ApplicationException(msg);
            }
            await SetAssFont(outputPath).ConfigureAwait(false);
        }

        /// <summary>
        /// Extracts the text subtitle.
        /// </summary>
        /// <param name="inputFiles">The input files.</param>
        /// <param name="protocol">The protocol.</param>
        /// <param name="subtitleStreamIndex">Index of the subtitle stream.</param>
        /// <param name="outputCodec">The output codec.</param>
        /// <param name="outputPath">The output path.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>Task.</returns>
        /// <exception cref="System.ArgumentException">Must use inputPath list overload</exception>
        private async Task ExtractTextSubtitle(string[] inputFiles, MediaProtocol protocol, int subtitleStreamIndex,
            string outputCodec, string outputPath, CancellationToken cancellationToken)
        {
            var semaphore = GetLock(outputPath);

            await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                if (!File.Exists(outputPath))
                {
                    await ExtractTextSubtitleInternal(_mediaEncoder.GetInputArgument(inputFiles, protocol), subtitleStreamIndex,
                            outputCodec, outputPath, cancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                semaphore.Release();
            }
        }

        private async Task ExtractTextSubtitleInternal(string inputPath, int subtitleStreamIndex,
            string outputCodec, string outputPath, CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(inputPath))
            {
                throw new ArgumentNullException("inputPath");
            }

            if (string.IsNullOrEmpty(outputPath))
            {
                throw new ArgumentNullException("outputPath");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(outputPath));

            var processArgs = string.Format("-i {0} -map 0:{1} -an -vn -c:s {2} \"{3}\"", inputPath,
                subtitleStreamIndex, outputCodec, outputPath);

            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,

                    RedirectStandardOutput = false,
                    RedirectStandardError = true,
                    RedirectStandardInput = true,

                    FileName = _mediaEncoder.EncoderPath,
                    Arguments = processArgs,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    ErrorDialog = false
                }
            };

            _logger.Info("{0} {1}", process.StartInfo.FileName, process.StartInfo.Arguments);

            var logFilePath = Path.Combine(_appPaths.LogDirectoryPath, "ffmpeg-sub-extract-" + Guid.NewGuid() + ".txt");
            Directory.CreateDirectory(Path.GetDirectoryName(logFilePath));

            var logFileStream = _fileSystem.GetFileStream(logFilePath, FileMode.Create, FileAccess.Write, FileShare.Read,
                true);

            try
            {
                process.Start();
            }
            catch (Exception ex)
            {
                logFileStream.Dispose();

                _logger.ErrorException("Error starting ffmpeg", ex);

                throw;
            }

            process.StandardError.BaseStream.CopyToAsync(logFileStream);

            var ranToCompletion = process.WaitForExit(60000);

            if (!ranToCompletion)
            {
                try
                {
                    _logger.Info("Killing ffmpeg subtitle extraction process");

                    process.StandardInput.WriteLine("q");
                    process.WaitForExit(1000);
                }
                catch (Exception ex)
                {
                    _logger.ErrorException("Error killing subtitle extraction process", ex);
                }
                finally
                {
                    logFileStream.Dispose();
                }
            }

            var exitCode = ranToCompletion ? process.ExitCode : -1;

            process.Dispose();

            var failed = false;

            if (exitCode == -1)
            {
                failed = true;

                try
                {
                    _logger.Info("Deleting extracted subtitle due to failure: {0}", outputPath);
                    _fileSystem.DeleteFile(outputPath);
                }
                catch (FileNotFoundException)
                {

                }
                catch (DirectoryNotFoundException)
                {

                }
                catch (IOException ex)
                {
                    _logger.ErrorException("Error deleting extracted subtitle {0}", ex, outputPath);
                }
            }
            else if (!File.Exists(outputPath))
            {
                failed = true;
            }

            if (failed)
            {
                var msg = string.Format("ffmpeg subtitle extraction failed for {0} to {1}", inputPath, outputPath);

                _logger.Error(msg);

                throw new ApplicationException(msg);
            }
            else
            {
                var msg = string.Format("ffmpeg subtitle extraction completed for {0} to {1}", inputPath, outputPath);

                _logger.Info(msg);
            }

            if (string.Equals(outputCodec, "ass", StringComparison.OrdinalIgnoreCase))
            {
                await SetAssFont(outputPath).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Sets the ass font.
        /// </summary>
        /// <param name="file">The file.</param>
        /// <returns>Task.</returns>
        private async Task SetAssFont(string file)
        {
            _logger.Info("Setting ass font within {0}", file);

            string text;
            Encoding encoding;

            using (var reader = new StreamReader(file, true))
            {
                encoding = reader.CurrentEncoding;

                text = await reader.ReadToEndAsync().ConfigureAwait(false);
            }

            var newText = text.Replace(",Arial,", ",Arial Unicode MS,");

            if (!string.Equals(text, newText))
            {
                using (var writer = new StreamWriter(file, false, encoding))
                {
                    writer.Write(newText);
                }
            }
        }

        private string GetSubtitleCachePath(string mediaPath, int subtitleStreamIndex, string outputSubtitleExtension)
        {
            var ticksParam = string.Empty;

            var date = _fileSystem.GetLastWriteTimeUtc(mediaPath);

            var filename = (mediaPath + "_" + subtitleStreamIndex.ToString(CultureInfo.InvariantCulture) + "_" + date.Ticks.ToString(CultureInfo.InvariantCulture) + ticksParam).GetMD5() + outputSubtitleExtension;

            var prefix = filename.Substring(0, 1);

            return Path.Combine(SubtitleCachePath, prefix, filename);
        }

        /// <summary>
        /// Gets the subtitle language encoding param.
        /// </summary>
        /// <param name="path">The path.</param>
        /// <returns>System.String.</returns>
        public string GetSubtitleFileCharacterSet(string path)
        {
            if (GetFileEncoding(path).Equals(Encoding.UTF8))
            {
                return string.Empty;
            }

            var charset = DetectCharset(path);

            if (!string.IsNullOrWhiteSpace(charset))
            {
                if (string.Equals(charset, "utf-8", StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }

                return charset;
            }

            return null;
        }

        public string GetSubtitleFileCharacterSetFromLanguage(string language)
        {
            switch (language.ToLower())
            {
                case "pol":
                case "cze":
                case "ces":
                case "slo":
                case "slk":
                case "hun":
                case "slv":
                case "srp":
                case "hrv":
                case "rum":
                case "ron":
                case "rup":
                case "alb":
                case "sqi":
                    return "windows-1250";
                case "ara":
                    return "windows-1256";
                case "heb":
                    return "windows-1255";
                case "grc":
                case "gre":
                    return "windows-1253";
                case "crh":
                case "ota":
                case "tur":
                    return "windows-1254";
                case "rus":
                    return "windows-1251";
                case "vie":
                    return "windows-1258";
                case "kor":
                    return "cp949";
                default:
                    return "windows-1252";
            }
        }

        private string DetectCharset(string path)
        {
            try
            {
                using (var file = _fileSystem.GetFileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    var detector = new CharsetDetector();
                    detector.Feed(file);
                    detector.DataEnd();

                    var charset = detector.Charset;

                    if (!string.IsNullOrWhiteSpace(charset))
                    {
                        _logger.Info("UniversalDetector detected charset {0} for {1}", charset, path);
                    }

                    return charset;
                }
            }
            catch (IOException ex)
            {
                _logger.ErrorException("Error attempting to determine subtitle charset from {0}", ex, path);
            }

            return null;
        }

        private Encoding GetFileEncoding(string srcFile)
        {
            // *** Detect byte order mark if any - otherwise assume default
            var buffer = new byte[5];

            using (var file = _fileSystem.GetFileStream(srcFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                file.Read(buffer, 0, 5);
            }

            if (buffer[0] == 0xef && buffer[1] == 0xbb && buffer[2] == 0xbf)
                return Encoding.UTF8;
            if (buffer[0] == 0xfe && buffer[1] == 0xff)
                return Encoding.Unicode;
            if (buffer[0] == 0 && buffer[1] == 0 && buffer[2] == 0xfe && buffer[3] == 0xff)
                return Encoding.UTF32;
            if (buffer[0] == 0x2b && buffer[1] == 0x2f && buffer[2] == 0x76)
                return Encoding.UTF7;

            // It's ok - anything aside from utf is ok since that's what we're looking for
            return Encoding.Default;
        }
    }
}
