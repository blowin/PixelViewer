﻿using CarinaStudio;
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace Carina.PixelViewer.Media.ImageRenderers
{
    /// <summary>
    /// Base implementation of <see cref="IImageRenderer"/> which renders image with 16-bit bayer filter pattern.
    /// </summary>
    class BayerPattern16ImageRenderer : BayerPatternImageRenderer
    {
        public BayerPattern16ImageRenderer() : base(new ImageFormat(ImageFormatCategory.Bayer, "Bayer_Pattern_16", true, new ImagePlaneDescriptor(2, 9, 16), new string[]{ "RAW16" }))
        { }


        /// <inheritdoc/>
        protected override unsafe ImageRenderingResult OnRender(IImageDataSource source, Stream imageStream, IBitmapBuffer bitmapBuffer, Func<int, int, int> colorComponentSelector, ImageRenderingOptions renderingOptions, IList<ImagePlaneOptions> planeOptions, CancellationToken cancellationToken)
        {
			// get parameters
			var width = bitmapBuffer.Width;
			var height = bitmapBuffer.Height;
			var pixelStride = planeOptions[0].PixelStride;
			var rowStride = planeOptions[0].RowStride;
			var effectiveBits = planeOptions[0].EffectiveBits;
			if (width <= 0 || height <= 0)
				throw new ArgumentException($"Invalid size: {width}x{height}.");
			if (pixelStride <= 0 || (pixelStride * width) > rowStride)
				throw new ArgumentException($"Invalid pixel/row stride: {pixelStride}/{rowStride}.");
			if (effectiveBits <= 8 || effectiveBits > 16)
				throw new ArgumentException($"Invalid effective bits: {effectiveBits}.");

			// prepare conversion
			var extractFunc = this.Create16BitColorExtraction(renderingOptions.ByteOrdering, effectiveBits);

			// render
			var baseColorTransformationTable = (ushort*)NativeMemory.Alloc(65536 * sizeof(ushort) * 3);
			var accuColor = stackalloc ulong[] { 0L, 0L, 0L };
			var accuPixelCount = stackalloc int[] { 0, 0, 0 };
			var wAccuColor = stackalloc ulong[] { 0L, 0L, 0L };
			var wAccuPixelCount = stackalloc int[] { 0, 0, 0 };
			var wLeft = width / 3;
			var wRight = width - wLeft;
			var wTop = height / 3;
			var wBottom = height - wTop;
			try
			{
				var colorTransformationTables = stackalloc ushort*[3] { 
					baseColorTransformationTable,
					baseColorTransformationTable + 65536,
					baseColorTransformationTable + 131072,
				};
				BuildColorTransformationTableUnsafe(colorTransformationTables[0], ImageRenderingOptions.GetValidRgbGain(renderingOptions.BlueGain));
				BuildColorTransformationTableUnsafe(colorTransformationTables[1], ImageRenderingOptions.GetValidRgbGain(renderingOptions.GreenGain));
				BuildColorTransformationTableUnsafe(colorTransformationTables[2], ImageRenderingOptions.GetValidRgbGain(renderingOptions.RedGain));
				bitmapBuffer.Memory.Pin(async (bitmapBaseAddress) =>
				{
					// render to 16-bit R/G/B
					var bitmapRowPtr = (byte*)bitmapBaseAddress;
					var bitmapRowStride = bitmapBuffer.RowBytes;
					byte[] row = new byte[rowStride];
					fixed (byte* rowPtr = row)
					{
						for (var y = 0; y < height; ++y, bitmapRowPtr += bitmapRowStride)
						{
							imageStream.Read(row, 0, rowStride);
							var pixelPtr = rowPtr;
							var bitmapPixelPtr = (ushort*)bitmapRowPtr;
							var isVerticalWeightedArea = (y >= wTop && y <= wBottom);
							for (var x = 0; x < width; ++x, pixelPtr += pixelStride, bitmapPixelPtr += 4)
							{
								var colorComponent = colorComponentSelector(x, y);
								var color = extractFunc(pixelPtr[0], pixelPtr[1]);
								accuColor[colorComponent] += color;
								++accuPixelCount[colorComponent];
								if (isVerticalWeightedArea && x >= wLeft && x <= wRight)
								{
									wAccuColor[colorComponent] += (ushort)(color << 1);
									wAccuPixelCount[colorComponent] += 2;
								}
								else
								{
									wAccuColor[colorComponent] += color;
									++wAccuPixelCount[colorComponent];
								}
								bitmapPixelPtr[colorComponent] = colorTransformationTables[colorComponent][color];
								bitmapPixelPtr[3] = 65535;
							}
							if (cancellationToken.IsCancellationRequested)
								break;
							if (y < height - 1)
								Array.Clear(row, 0, rowStride);
						}
					}
				});
			}
			finally
			{
				NativeMemory.Free(baseColorTransformationTable);
			}

			// complete
			return new ImageRenderingResult()
			{
				MeanOfBlue = accuColor[BlueColorComponent] / (double)accuPixelCount[BlueColorComponent],
				MeanOfGreen = accuColor[GreenColorComponent] / (double)accuPixelCount[GreenColorComponent],
				MeanOfRed = accuColor[RedColorComponent] / (double)accuPixelCount[RedColorComponent],
				WeightedMeanOfBlue = wAccuColor[BlueColorComponent] / (double)wAccuPixelCount[BlueColorComponent],
				WeightedMeanOfGreen = wAccuColor[GreenColorComponent] / (double)wAccuPixelCount[GreenColorComponent],
				WeightedMeanOfRed = wAccuColor[RedColorComponent] / (double)wAccuPixelCount[RedColorComponent],
			};
		}
    }
}
