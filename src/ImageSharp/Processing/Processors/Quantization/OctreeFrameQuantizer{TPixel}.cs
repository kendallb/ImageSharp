// Copyright (c) Six Labors and contributors.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SixLabors.ImageSharp.Advanced;
using SixLabors.ImageSharp.PixelFormats;

namespace SixLabors.ImageSharp.Processing.Processors.Quantization
{
    /// <summary>
    /// Encapsulates methods to calculate the color palette if an image using an Octree pattern.
    /// <see href="http://msdn.microsoft.com/en-us/library/aa479306.aspx"/>
    /// </summary>
    /// <typeparam name="TPixel">The pixel format.</typeparam>
    internal sealed class OctreeFrameQuantizer<TPixel> : FrameQuantizer<TPixel>
        where TPixel : struct, IPixel<TPixel>
    {
        /// <summary>
        /// Maximum allowed color depth
        /// </summary>
        private readonly int colors;

        /// <summary>
        /// Stores the tree
        /// </summary>
        private readonly Octree octree;

        /// <summary>
        /// The reduced image palette
        /// </summary>
        private TPixel[] palette;

        /// <summary>
        /// Initializes a new instance of the <see cref="OctreeFrameQuantizer{TPixel}"/> class.
        /// </summary>
        /// <param name="configuration">The configuration which allows altering default behaviour or extending the library.</param>
        /// <param name="quantizer">The octree quantizer</param>
        /// <remarks>
        /// The Octree quantizer is a two pass algorithm. The initial pass sets up the Octree,
        /// the second pass quantizes a color based on the nodes in the tree
        /// </remarks>
        public OctreeFrameQuantizer(Configuration configuration, OctreeQuantizer quantizer)
            : this(configuration, quantizer, quantizer.MaxColors)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="OctreeFrameQuantizer{TPixel}"/> class.
        /// </summary>
        /// <param name="configuration">The configuration which allows altering default behaviour or extending the library.</param>
        /// <param name="quantizer">The octree quantizer.</param>
        /// <param name="maxColors">The maximum number of colors to hold in the color palette.</param>
        /// <remarks>
        /// The Octree quantizer is a two pass algorithm. The initial pass sets up the Octree,
        /// the second pass quantizes a color based on the nodes in the tree
        /// </remarks>
        public OctreeFrameQuantizer(Configuration configuration, OctreeQuantizer quantizer, int maxColors)
            : base(configuration, quantizer, false)
        {
            this.colors = maxColors;
            this.octree = new Octree(ImageMaths.GetBitsNeededForColorDepth(this.colors).Clamp(1, 8));
        }

        /// <inheritdoc/>
        protected override void FirstPass(ImageFrame<TPixel> source, Rectangle bounds)
        {
            // Loop through each row
            int offset = bounds.Left;
            for (int y = bounds.Top; y < bounds.Bottom; y++)
            {
                Span<TPixel> row = source.GetPixelRowSpan(y);
                ref TPixel scanBaseRef = ref MemoryMarshal.GetReference(row);

                // And loop through each column
                for (int x = bounds.Left; x < bounds.Right; x++)
                {
                    ref TPixel pixel = ref Unsafe.Add(ref scanBaseRef, x - offset);

                    // Add the color to the Octree
                    this.octree.AddColor(ref pixel);
                }
            }
        }

        /// <inheritdoc/>
        [MethodImpl(InliningOptions.ShortMethod)]
        protected override byte GetQuantizedColor(TPixel color, ReadOnlySpan<TPixel> palette, out TPixel match)
        {
            if (!this.DoDither)
            {
                var index = (byte)this.octree.GetPaletteIndex(ref color);
                match = palette[index];
                return index;
            }

            return base.GetQuantizedColor(color, palette, out match);
        }

        internal ReadOnlyMemory<TPixel> AotGetPalette() => this.GenerateQuantizedPalette();

        /// <inheritdoc/>
        [MethodImpl(InliningOptions.ShortMethod)]
        protected override ReadOnlyMemory<TPixel> GenerateQuantizedPalette()
            => this.palette ?? (this.palette = this.octree.Palletize(this.colors));

        /// <summary>
        /// Class which does the actual quantization
        /// </summary>
        private sealed class Octree
        {
            /// <summary>
            /// Mask used when getting the appropriate pixels for a given node
            /// </summary>
            // ReSharper disable once StaticMemberInGenericType
            private static readonly int[] Mask = { 0x80, 0x40, 0x20, 0x10, 0x08, 0x04, 0x02, 0x01 };

            /// <summary>
            /// The root of the Octree
            /// </summary>
            private readonly OctreeNode root;

            /// <summary>
            /// Maximum number of significant bits in the image
            /// </summary>
            private readonly int maxColorBits;

            /// <summary>
            /// Store the last node quantized
            /// </summary>
            private OctreeNode previousNode;

            /// <summary>
            /// Cache the previous color quantized
            /// </summary>
            private TPixel previousColor;

            /// <summary>
            /// Initializes a new instance of the <see cref="Octree"/> class.
            /// </summary>
            /// <param name="maxColorBits">
            /// The maximum number of significant bits in the image
            /// </param>
            public Octree(int maxColorBits)
            {
                this.maxColorBits = maxColorBits;
                this.Leaves = 0;
                this.ReducibleNodes = new OctreeNode[9];
                this.root = new OctreeNode(0, this.maxColorBits, this);
                this.previousColor = default;
                this.previousNode = null;
            }

            /// <summary>
            /// Gets or sets the number of leaves in the tree
            /// </summary>
            public int Leaves
            {
                [MethodImpl(InliningOptions.ShortMethod)]
                get;

                [MethodImpl(InliningOptions.ShortMethod)]
                set;
            }

            /// <summary>
            /// Gets the array of reducible nodes
            /// </summary>
            private OctreeNode[] ReducibleNodes
            {
                [MethodImpl(InliningOptions.ShortMethod)]
                get;
            }

            /// <summary>
            /// Add a given color value to the Octree
            /// </summary>
            /// <param name="pixel">The pixel data.</param>
            public void AddColor(ref TPixel pixel)
            {
                // Check if this request is for the same color as the last
                if (this.previousColor.Equals(pixel))
                {
                    // If so, check if I have a previous node setup. This will only occur if the first color in the image
                    // happens to be black, with an alpha component of zero.
                    if (this.previousNode is null)
                    {
                        this.previousColor = pixel;
                        this.root.AddColor(ref pixel, this.maxColorBits, 0, this);
                    }
                    else
                    {
                        // Just update the previous node
                        this.previousNode.Increment(ref pixel);
                    }
                }
                else
                {
                    this.previousColor = pixel;
                    this.root.AddColor(ref pixel, this.maxColorBits, 0, this);
                }
            }

            /// <summary>
            /// Convert the nodes in the Octree to a palette with a maximum of colorCount colors
            /// </summary>
            /// <param name="colorCount">The maximum number of colors</param>
            /// <returns>
            /// An <see cref="List{TPixel}"/> with the palletized colors
            /// </returns>
            [MethodImpl(InliningOptions.ShortMethod)]
            public TPixel[] Palletize(int colorCount)
            {
                while (this.Leaves > colorCount - 1)
                {
                    this.Reduce();
                }

                // Now palletize the nodes
                var palette = new TPixel[colorCount];

                int paletteIndex = 0;
                this.root.ConstructPalette(palette, ref paletteIndex);

                // And return the palette
                return palette;
            }

            /// <summary>
            /// Get the palette index for the passed color
            /// </summary>
            /// <param name="pixel">The pixel data.</param>
            /// <returns>
            /// The <see cref="int"/>.
            /// </returns>
            [MethodImpl(InliningOptions.ShortMethod)]
            public int GetPaletteIndex(ref TPixel pixel) => this.root.GetPaletteIndex(ref pixel, 0);

            /// <summary>
            /// Keep track of the previous node that was quantized
            /// </summary>
            /// <param name="node">
            /// The node last quantized
            /// </param>
            [MethodImpl(InliningOptions.ShortMethod)]
            public void TrackPrevious(OctreeNode node) => this.previousNode = node;

            /// <summary>
            /// Reduce the depth of the tree
            /// </summary>
            private void Reduce()
            {
                // Find the deepest level containing at least one reducible node
                int index = this.maxColorBits - 1;
                while ((index > 0) && (this.ReducibleNodes[index] is null))
                {
                    index--;
                }

                // Reduce the node most recently added to the list at level 'index'
                OctreeNode node = this.ReducibleNodes[index];
                this.ReducibleNodes[index] = node.NextReducible;

                // Decrement the leaf count after reducing the node
                this.Leaves -= node.Reduce();

                // And just in case I've reduced the last color to be added, and the next color to
                // be added is the same, invalidate the previousNode...
                this.previousNode = null;
            }

            /// <summary>
            /// Class which encapsulates each node in the tree
            /// </summary>
            public sealed class OctreeNode
            {
                /// <summary>
                /// Pointers to any child nodes
                /// </summary>
                private readonly OctreeNode[] children;

                /// <summary>
                /// Flag indicating that this is a leaf node
                /// </summary>
                private bool leaf;

                /// <summary>
                /// Number of pixels in this node
                /// </summary>
                private int pixelCount;

                /// <summary>
                /// Red component
                /// </summary>
                private int red;

                /// <summary>
                /// Green Component
                /// </summary>
                private int green;

                /// <summary>
                /// Blue component
                /// </summary>
                private int blue;

                /// <summary>
                /// The index of this node in the palette
                /// </summary>
                private int paletteIndex;

                /// <summary>
                /// Initializes a new instance of the <see cref="OctreeNode"/> class.
                /// </summary>
                /// <param name="level">
                /// The level in the tree = 0 - 7
                /// </param>
                /// <param name="colorBits">
                /// The number of significant color bits in the image
                /// </param>
                /// <param name="octree">
                /// The tree to which this node belongs
                /// </param>
                public OctreeNode(int level, int colorBits, Octree octree)
                {
                    // Construct the new node
                    this.leaf = level == colorBits;

                    this.red = this.green = this.blue = 0;
                    this.pixelCount = 0;

                    // If a leaf, increment the leaf count
                    if (this.leaf)
                    {
                        octree.Leaves++;
                        this.NextReducible = null;
                        this.children = null;
                    }
                    else
                    {
                        // Otherwise add this to the reducible nodes
                        this.NextReducible = octree.ReducibleNodes[level];
                        octree.ReducibleNodes[level] = this;
                        this.children = new OctreeNode[8];
                    }
                }

                /// <summary>
                /// Gets the next reducible node
                /// </summary>
                public OctreeNode NextReducible
                {
                    [MethodImpl(InliningOptions.ShortMethod)]
                    get;
                }

                /// <summary>
                /// Add a color into the tree
                /// </summary>
                /// <param name="pixel">The pixel color</param>
                /// <param name="colorBits">The number of significant color bits</param>
                /// <param name="level">The level in the tree</param>
                /// <param name="octree">The tree to which this node belongs</param>
                public void AddColor(ref TPixel pixel, int colorBits, int level, Octree octree)
                {
                    // Update the color information if this is a leaf
                    if (this.leaf)
                    {
                        this.Increment(ref pixel);

                        // Setup the previous node
                        octree.TrackPrevious(this);
                    }
                    else
                    {
                        // Go to the next level down in the tree
                        int shift = 7 - level;
                        Rgba32 rgba = default;
                        pixel.ToRgba32(ref rgba);

                        int index = ((rgba.B & Mask[level]) >> (shift - 2))
                                    | ((rgba.G & Mask[level]) >> (shift - 1))
                                    | ((rgba.R & Mask[level]) >> shift);

                        OctreeNode child = this.children[index];
                        if (child is null)
                        {
                            // Create a new child node and store it in the array
                            child = new OctreeNode(level + 1, colorBits, octree);
                            this.children[index] = child;
                        }

                        // Add the color to the child node
                        child.AddColor(ref pixel, colorBits, level + 1, octree);
                    }
                }

                /// <summary>
                /// Reduce this node by removing all of its children
                /// </summary>
                /// <returns>The number of leaves removed</returns>
                public int Reduce()
                {
                    this.red = this.green = this.blue = 0;
                    int childNodes = 0;

                    // Loop through all children and add their information to this node
                    for (int index = 0; index < 8; index++)
                    {
                        OctreeNode child = this.children[index];
                        if (child != null)
                        {
                            this.red += child.red;
                            this.green += child.green;
                            this.blue += child.blue;
                            this.pixelCount += child.pixelCount;
                            ++childNodes;
                            this.children[index] = null;
                        }
                    }

                    // Now change this to a leaf node
                    this.leaf = true;

                    // Return the number of nodes to decrement the leaf count by
                    return childNodes - 1;
                }

                /// <summary>
                /// Traverse the tree, building up the color palette
                /// </summary>
                /// <param name="palette">The palette</param>
                /// <param name="index">The current palette index</param>
                [MethodImpl(InliningOptions.ColdPath)]
                public void ConstructPalette(TPixel[] palette, ref int index)
                {
                    if (this.leaf)
                    {
                        // Set the color of the palette entry
                        var vector = Vector3.Clamp(new Vector3(this.red, this.green, this.blue) / this.pixelCount, Vector3.Zero, new Vector3(255));
                        TPixel pixel = default;
                        pixel.FromRgba32(new Rgba32((byte)vector.X, (byte)vector.Y, (byte)vector.Z, byte.MaxValue));
                        palette[index] = pixel;

                        // Consume the next palette index
                        this.paletteIndex = index++;
                    }
                    else
                    {
                        // Loop through children looking for leaves
                        for (int i = 0; i < 8; i++)
                        {
                            this.children[i]?.ConstructPalette(palette, ref index);
                        }
                    }
                }

                /// <summary>
                /// Return the palette index for the passed color
                /// </summary>
                /// <param name="pixel">The pixel data.</param>
                /// <param name="level">The level.</param>
                /// <returns>
                /// The <see cref="int"/> representing the index of the pixel in the palette.
                /// </returns>
                [MethodImpl(InliningOptions.ColdPath)]
                public int GetPaletteIndex(ref TPixel pixel, int level)
                {
                    int index = this.paletteIndex;

                    if (!this.leaf)
                    {
                        int shift = 7 - level;
                        Rgba32 rgba = default;
                        pixel.ToRgba32(ref rgba);

                        int pixelIndex = ((rgba.B & Mask[level]) >> (shift - 2))
                                         | ((rgba.G & Mask[level]) >> (shift - 1))
                                         | ((rgba.R & Mask[level]) >> shift);

                        OctreeNode child = this.children[pixelIndex];
                        if (child != null)
                        {
                            index = child.GetPaletteIndex(ref pixel, level + 1);
                        }
                        else
                        {
                            // TODO: Throw helper.
                            throw new Exception($"Cannot retrieve a pixel at the given index {pixelIndex}.");
                        }
                    }

                    return index;
                }

                /// <summary>
                /// Increment the pixel count and add to the color information
                /// </summary>
                /// <param name="pixel">The pixel to add.</param>
                [MethodImpl(InliningOptions.ShortMethod)]
                public void Increment(ref TPixel pixel)
                {
                    Rgba32 rgba = default;
                    pixel.ToRgba32(ref rgba);
                    this.pixelCount++;
                    this.red += rgba.R;
                    this.green += rgba.G;
                    this.blue += rgba.B;
                }
            }
        }
    }
}
