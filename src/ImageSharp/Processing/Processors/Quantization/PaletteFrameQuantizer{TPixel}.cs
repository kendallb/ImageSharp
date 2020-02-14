// Copyright (c) Six Labors and contributors.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Runtime.CompilerServices;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing.Processors.Dithering;

namespace SixLabors.ImageSharp.Processing.Processors.Quantization
{
    /// <summary>
    /// Encapsulates methods to create a quantized image based upon the given palette.
    /// <see href="http://msdn.microsoft.com/en-us/library/aa479306.aspx"/>
    /// </summary>
    /// <typeparam name="TPixel">The pixel format.</typeparam>
    internal sealed class PaletteFrameQuantizer<TPixel> : FrameQuantizer<TPixel>
        where TPixel : struct, IPixel<TPixel>
    {
        /// <summary>
        /// The reduced image palette.
        /// </summary>
        private readonly ReadOnlyMemory<TPixel> palette;

        /// <summary>
        /// Initializes a new instance of the <see cref="PaletteFrameQuantizer{TPixel}"/> class.
        /// </summary>
        /// <param name="configuration">The configuration which allows altering default behaviour or extending the library.</param>
        /// <param name="diffuser">The palette quantizer.</param>
        /// <param name="colors">An array of all colors in the palette.</param>
        public PaletteFrameQuantizer(Configuration configuration, IDither diffuser, ReadOnlyMemory<TPixel> colors)
            : base(configuration, diffuser, true) => this.palette = colors;

        /// <inheritdoc/>
        [MethodImpl(InliningOptions.ShortMethod)]
        protected override ReadOnlyMemory<TPixel> GenerateQuantizedPalette() => this.palette;
    }
}
