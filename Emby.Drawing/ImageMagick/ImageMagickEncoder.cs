﻿using System.Threading.Tasks;
using ImageMagickSharp;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Drawing;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Logging;
using System;
using System.IO;
using System.Linq;
using CommonIO;
using MediaBrowser.Controller.Configuration;

namespace Emby.Drawing.ImageMagick
{
    public class ImageMagickEncoder : IImageEncoder
    {
        private readonly ILogger _logger;
        private readonly IApplicationPaths _appPaths;
        private readonly IHttpClient _httpClient;
        private readonly IFileSystem _fileSystem;
        private readonly IServerConfigurationManager _config;

        public ImageMagickEncoder(ILogger logger, IApplicationPaths appPaths, IHttpClient httpClient, IFileSystem fileSystem, IServerConfigurationManager config)
        {
            _logger = logger;
            _appPaths = appPaths;
            _httpClient = httpClient;
            _fileSystem = fileSystem;
            _config = config;

            LogVersion();
        }

        public string[] SupportedInputFormats
        {
            get
            {
                // Some common file name extensions for RAW picture files include: .cr2, .crw, .dng, .nef, .orf, .rw2, .pef, .arw, .sr2, .srf, and .tif.
                return new[]
                {
                    "tiff", 
                    "jpeg", 
                    "jpg", 
                    "png", 
                    "aiff", 
                    "cr2", 
                    "crw", 
                    "dng", 
                    "nef", 
                    "orf", 
                    "pef", 
                    "arw", 
                    "webp",
                    "gif",
                    "bmp"
                };
            }
        }

        public ImageFormat[] SupportedOutputFormats
        {
            get
            {
                if (_webpAvailable)
                {
                    return new[] { ImageFormat.Webp, ImageFormat.Gif, ImageFormat.Jpg, ImageFormat.Png };
                }
                return new[] { ImageFormat.Gif, ImageFormat.Jpg, ImageFormat.Png };
            }
        }

        private void LogVersion()
        {
            _logger.Info("ImageMagick version: " + Wand.VersionString);
            TestWebp();
            Wand.SetMagickThreadCount(1);
        }

        private bool _webpAvailable = true;
        private void TestWebp()
        {
            try
            {
                var tmpPath = Path.Combine(_appPaths.TempDirectory, Guid.NewGuid() + ".webp");
                _fileSystem.CreateDirectory(Path.GetDirectoryName(tmpPath));

                using (var wand = new MagickWand(1, 1, new PixelWand("none", 1)))
                {
                    wand.SaveImage(tmpPath);
                }
            }
            catch 
            {
                //_logger.ErrorException("Error loading webp: ", ex);
                _webpAvailable = false;
            }
        }

        public void CropWhiteSpace(string inputPath, string outputPath)
        {
            CheckDisposed();

            using (var wand = new MagickWand(inputPath))
            {
                wand.CurrentImage.TrimImage(10);
                wand.SaveImage(outputPath);
            }
            SaveDelay();
        }

        public ImageSize GetImageSize(string path)
        {
            CheckDisposed();

            using (var wand = new MagickWand())
            {
                wand.PingImage(path);
                var img = wand.CurrentImage;

                return new ImageSize
                {
                    Width = img.Width,
                    Height = img.Height
                };
            }
        }

        private bool HasTransparency(string path)
        {
            var ext = Path.GetExtension(path);

            return string.Equals(ext, ".png", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(ext, ".webp", StringComparison.OrdinalIgnoreCase);
        }

        public void EncodeImage(string inputPath, string outputPath, int width, int height, int quality, ImageProcessingOptions options)
        {
            if (string.IsNullOrWhiteSpace(options.BackgroundColor) || !HasTransparency(inputPath))
            {
                using (var originalImage = new MagickWand(inputPath))
                {
                    ScaleImage(originalImage, width, height);

                    DrawIndicator(originalImage, width, height, options);

                    originalImage.CurrentImage.CompressionQuality = quality;
                    originalImage.CurrentImage.StripImage();

                    originalImage.SaveImage(outputPath);
                }
            }
            else
            {
                using (var wand = new MagickWand(width, height, options.BackgroundColor))
                {
                    using (var originalImage = new MagickWand(inputPath))
                    {
                        ScaleImage(originalImage, width, height);

                        wand.CurrentImage.CompositeImage(originalImage, CompositeOperator.OverCompositeOp, 0, 0);
                        DrawIndicator(wand, width, height, options);

                        wand.CurrentImage.CompressionQuality = quality;
                        wand.CurrentImage.StripImage();

                        wand.SaveImage(outputPath);
                    }
                }
            }
            SaveDelay();
        }

        private void ScaleImage(MagickWand wand, int width, int height)
        {
            if (_config.Configuration.EnableHighQualityImageScaling)
            {
                wand.CurrentImage.ResizeImage(width, height);
            }
            else
            {
                wand.CurrentImage.ScaleImage(width, height);
            }
        }

        /// <summary>
        /// Draws the indicator.
        /// </summary>
        /// <param name="wand">The wand.</param>
        /// <param name="imageWidth">Width of the image.</param>
        /// <param name="imageHeight">Height of the image.</param>
        /// <param name="options">The options.</param>
        private void DrawIndicator(MagickWand wand, int imageWidth, int imageHeight, ImageProcessingOptions options)
        {
            if (!options.AddPlayedIndicator && !options.UnplayedCount.HasValue && options.PercentPlayed.Equals(0))
            {
                return;
            }

            try
            {
                if (options.AddPlayedIndicator)
                {
                    var currentImageSize = new ImageSize(imageWidth, imageHeight);

                    var task = new PlayedIndicatorDrawer(_appPaths, _httpClient, _fileSystem).DrawPlayedIndicator(wand, currentImageSize);
                    Task.WaitAll(task);
                }
                else if (options.UnplayedCount.HasValue)
                {
                    var currentImageSize = new ImageSize(imageWidth, imageHeight);

                    new UnplayedCountIndicator(_appPaths, _fileSystem).DrawUnplayedCountIndicator(wand, currentImageSize, options.UnplayedCount.Value);
                }

                if (options.PercentPlayed > 0)
                {
                    new PercentPlayedDrawer().Process(wand, options.PercentPlayed);
                }
            }
            catch (Exception ex)
            {
                _logger.ErrorException("Error drawing indicator overlay", ex);
            }
        }

        public void CreateImageCollage(ImageCollageOptions options)
        {
            double ratio = options.Width;
            ratio /= options.Height;

            if (ratio >= 1.4)
            {
                new StripCollageBuilder(_appPaths, _fileSystem).BuildThumbCollage(options.InputPaths.ToList(), options.OutputPath, options.Width, options.Height, options.Text);
            }
            else if (ratio >= .9)
            {
                new StripCollageBuilder(_appPaths, _fileSystem).BuildSquareCollage(options.InputPaths.ToList(), options.OutputPath, options.Width, options.Height, options.Text);
            }
            else
            {
                new StripCollageBuilder(_appPaths, _fileSystem).BuildPosterCollage(options.InputPaths.ToList(), options.OutputPath, options.Width, options.Height, options.Text);
            }

            SaveDelay();
        }

        private void SaveDelay()
        {
            // For some reason the images are not always getting released right away
            var task = Task.Delay(300);
            Task.WaitAll(task);
        }

        public string Name
        {
            get { return "ImageMagick"; }
        }

        private bool _disposed;
        public void Dispose()
        {
            _disposed = true;
            Wand.CloseEnvironment();
        }

        private void CheckDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(GetType().Name);
            }
        }

        public bool SupportsImageCollageCreation
        {
            get { return true; }
        }

        public bool SupportsImageEncoding
        {
            get { return true; }
        }
    }
}
