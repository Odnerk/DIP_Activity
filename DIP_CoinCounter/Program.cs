using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;

namespace DIP_CoinCounter
{
    class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("=================================================");
            Console.WriteLine("   DIP Coin Counter & Denomination Classifier   ");
            Console.WriteLine("=================================================\n");

            // Resolve input image path
            string inputImagePath = ResolveImagePath(args);
            if (string.IsNullOrEmpty(inputImagePath) || !File.Exists(inputImagePath))
            {
                Console.WriteLine("Error: Could not locate 'coins.jpeg'.");
                Console.WriteLine("Usage: DIP_CoinCounter [path_to_coins.jpeg]");
                return;
            }

            Console.WriteLine($"Loading source image: {Path.GetFullPath(inputImagePath)}");

            List<int> coinPixelAreas = new List<int>();
            string outputDirectory = Path.GetDirectoryName(Path.GetFullPath(inputImagePath)) ?? ".";
            string binarizedImagePath = Path.Combine(outputDirectory, "coins_binarized.png");

            using (Bitmap sourceBitmap = new Bitmap(inputImagePath))
            {
                int imageWidth = sourceBitmap.Width;
                int imageHeight = sourceBitmap.Height;

                Console.WriteLine($"Image Dimensions: {imageWidth} x {imageHeight} pixels");
                Console.WriteLine("Stage 1: Grayscale conversion & threshold binarization...");

                // Binary mask: true indicates coin foreground pixel (dark), false indicates background (light)
                bool[,] isForeground = new bool[imageWidth, imageHeight];
                const int luminanceThreshold = 150;

                for (int y = 0; y < imageHeight; y++)
                {
                    for (int x = 0; x < imageWidth; x++)
                    {
                        Color pixelColor = sourceBitmap.GetPixel(x, y);
                        // Standard luminance formula: Y = 0.299*R + 0.587*G + 0.114*B
                        int luminance = (int)(pixelColor.R * 0.299 + pixelColor.G * 0.587 + pixelColor.B * 0.114);
                        isForeground[x, y] = luminance <= luminanceThreshold;
                    }
                }

                Console.WriteLine("Stage 2: Morphological dilation for reflection noise reduction...");
                bool[,] cleanedForeground = new bool[imageWidth, imageHeight];
                int[] neighborXOffsets = { -1, 1, 0, 0, -1, -1, 1, 1 };
                int[] neighborYOffsets = { 0, 0, -1, 1, -1, 1, -1, 1 };

                for (int y = 0; y < imageHeight; y++)
                {
                    for (int x = 0; x < imageWidth; x++)
                    {
                        if (isForeground[x, y])
                        {
                            cleanedForeground[x, y] = true;
                        }
                        else
                        {
                            // Expand foreground if adjacent to any coin pixel to seal reflection holes
                            bool adjacentToCoin = false;
                            for (int i = 0; i < 8; i++)
                            {
                                int nx = x + neighborXOffsets[i];
                                int ny = y + neighborYOffsets[i];
                                if (nx >= 0 && nx < imageWidth && ny >= 0 && ny < imageHeight && isForeground[nx, ny])
                                {
                                    adjacentToCoin = true;
                                    break;
                                }
                            }
                            cleanedForeground[x, y] = adjacentToCoin;
                        }
                    }
                }

                // Save binarized image preview
                using (Bitmap binarizedBitmap = new Bitmap(imageWidth, imageHeight))
                {
                    for (int y = 0; y < imageHeight; y++)
                    {
                        for (int x = 0; x < imageWidth; x++)
                        {
                            binarizedBitmap.SetPixel(x, y, cleanedForeground[x, y] ? Color.Black : Color.White);
                        }
                    }
                    binarizedBitmap.Save(binarizedImagePath, ImageFormat.Png);
                    string flattenedImagePath = Path.Combine(outputDirectory, "coins_flattened.png");
                    binarizedBitmap.Save(flattenedImagePath, ImageFormat.Png);
                    Console.WriteLine($"Saved binarized image to: {binarizedImagePath}");
                }

                Console.WriteLine("Stage 3: Connected-Component Analysis using Depth-First Search (DFS)...");
                bool[,] visitedPixels = new bool[imageWidth, imageHeight];
                List<List<Point>> detectedCoinBlobs = new List<List<Point>>();

                for (int y = 0; y < imageHeight; y++)
                {
                    for (int x = 0; x < imageWidth; x++)
                    {
                        if (visitedPixels[x, y] || !cleanedForeground[x, y])
                            continue;

                        // Discover new connected component (coin island)
                        List<Point> currentBlob = new List<Point>();
                        Stack<Point> dfsStack = new Stack<Point>();
                        dfsStack.Push(new Point(x, y));

                        while (dfsStack.Count > 0)
                        {
                            Point currentPixel = dfsStack.Pop();
                            int cx = currentPixel.X;
                            int cy = currentPixel.Y;

                            if (visitedPixels[cx, cy])
                                continue;

                            visitedPixels[cx, cy] = true;
                            currentBlob.Add(currentPixel);

                            for (int i = 0; i < 8; i++)
                            {
                                int nx = cx + neighborXOffsets[i];
                                int ny = cy + neighborYOffsets[i];

                                if (nx >= 0 && nx < imageWidth && ny >= 0 && ny < imageHeight &&
                                    !visitedPixels[nx, ny] && cleanedForeground[nx, ny])
                                {
                                    dfsStack.Push(new Point(nx, ny));
                                }
                            }
                        }

                        detectedCoinBlobs.Add(currentBlob);
                    }
                }

                // Filter out small noise artifacts (minimum 1000 pixels)
                const int minCoinPixelArea = 1000;
                coinPixelAreas = detectedCoinBlobs
                    .Where(blob => blob.Count > minCoinPixelArea)
                    .Select(blob => blob.Count)
                    .ToList();

                Console.WriteLine($"Found {detectedCoinBlobs.Count} total components ({coinPixelAreas.Count} valid coins after noise filter).\n");
            }

            // Stage 4: Size-based classification and monetary total calculation
            ClassifyAndDisplayResults(coinPixelAreas);
        }

        static void ClassifyAndDisplayResults(List<int> coinPixelAreas)
        {
            if (coinPixelAreas.Count == 0)
            {
                Console.WriteLine("No valid coins found to classify.");
                return;
            }

            coinPixelAreas.Sort();

            const int denominationCount = 5;
            var sizeGaps = new List<(int gapSize, int splitIndex)>();

            for (int i = 1; i < coinPixelAreas.Count; i++)
            {
                sizeGaps.Add((coinPixelAreas[i] - coinPixelAreas[i - 1], i));
            }

            // Find the 4 largest size differences (natural break boundaries)
            var clusterBoundaries = sizeGaps
                .OrderByDescending(g => g.gapSize)
                .Take(denominationCount - 1)
                .Select(g => g.splitIndex)
                .OrderBy(idx => idx)
                .ToList();

            List<List<int>> denominationGroups = new List<List<int>>();
            List<int> currentGroup = new List<int>();
            int currentBoundaryIndex = 0;

            for (int i = 0; i < coinPixelAreas.Count; i++)
            {
                if (currentBoundaryIndex < clusterBoundaries.Count && i == clusterBoundaries[currentBoundaryIndex])
                {
                    denominationGroups.Add(currentGroup);
                    currentGroup = new List<int>();
                    currentBoundaryIndex++;
                }
                currentGroup.Add(coinPixelAreas[i]);
            }
            denominationGroups.Add(currentGroup);

            // Philippine Peso denomination definitions
            string[] denominationLabels = { "5 Sentimos (5¢)", "10 Sentimos (10¢)", "25 Sentimos (25¢)", "1 Piso (₱1.00)", "5 Piso (₱5.00)" };
            decimal[] denominationValues = { 0.05m, 0.10m, 0.25m, 1.00m, 5.00m };

            Console.WriteLine("==========================================================================");
            Console.WriteLine(string.Format("{0,-20} | {1,-10} | {2,-12} | {3,-8} | {4,-10}",
                "Denomination", "Unit Value", "Avg Area (px)", "Quantity", "Subtotal"));
            Console.WriteLine("--------------------------------------------------------------------------");

            int totalCoinsCount = 0;
            decimal totalMonetarySum = 0.00m;

            for (int i = 0; i < denominationGroups.Count; i++)
            {
                string label = i < denominationLabels.Length ? denominationLabels[i] : $"Group {i + 1}";
                decimal unitValue = i < denominationValues.Length ? denominationValues[i] : 0.00m;
                int quantity = denominationGroups[i].Count;
                double averageArea = denominationGroups[i].Average();
                decimal subtotal = quantity * unitValue;

                totalCoinsCount += quantity;
                totalMonetarySum += subtotal;

                Console.WriteLine(string.Format("{0,-20} | {1,-10:C2} | {2,-12:F0} | {3,-8} | {4,-10:C2}",
                    label, unitValue, averageArea, quantity, subtotal));
            }

            Console.WriteLine("==========================================================================");
            Console.WriteLine($"Total Coins Counted     : {totalCoinsCount}");
            Console.WriteLine($"Total Denomination Value: ₱{totalMonetarySum:F2} PHP");
            Console.WriteLine("==========================================================================");
        }

        static string ResolveImagePath(string[] args)
        {
            if (args.Length > 0 && File.Exists(args[0]))
                return args[0];

            string[] candidatePaths = {
                "coins.jpeg",
                "../coins.jpeg",
                "../../coins.jpeg",
                "executables/coins.jpeg",
                "../executables/coins.jpeg"
            };

            foreach (var path in candidatePaths)
            {
                if (File.Exists(path))
                    return path;
            }

            return "coins.jpeg";
        }
    }
}
