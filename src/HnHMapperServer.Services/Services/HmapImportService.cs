using System.Diagnostics;
using System.Threading.Channels;
using HnHMapperServer.Core.Interfaces;
using HnHMapperServer.Core.Models;
using HnHMapperServer.Services.Interfaces;
using Microsoft.Extensions.Logging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace HnHMapperServer.Services.Services;

/// <summary>
/// Represents a rendered grid ready for I/O operations.
/// Used to pass data from producer (rendering) to consumer (saving).
/// </summary>
internal sealed record RenderedGrid(
    HmapGridData SourceGrid,
    GridData GridData,
    Image<Rgba32> TileImage,
    string RelativePath,
    string FullPath);

/// <summary>
/// Helper class to track import progress with timing and overall percentage
/// </summary>
internal class ImportProgressTracker
{
    private readonly IProgress<HmapImportProgress>? _progress;
    private readonly Stopwatch _stopwatch;
    private readonly Stopwatch _phaseStopwatch;
    private DateTime _lastReportTime = DateTime.MinValue;
    private int _lastReportedItem = 0;

    // Phase weights for overall progress calculation (must sum to 100)
    private const int PHASE_PARSE = 2;           // Phase 1: Parsing
    private const int PHASE_FETCH_TILES = 18;    // Phase 2: Fetching tiles
    private const int PHASE_IMPORT_GRIDS = 60;   // Phase 3: Importing grids (longest)
    private const int PHASE_ZOOM_LEVELS = 15;    // Phase 4: Generating zoom
    private const int PHASE_MARKERS = 5;         // Phase 5: Importing markers

    public int CurrentPhase { get; private set; }
    public const int TotalPhases = 5;

    private double _completedPhaseWeight = 0;
    private double _currentPhaseWeight = 0;

    public ImportProgressTracker(IProgress<HmapImportProgress>? progress)
    {
        _progress = progress;
        _stopwatch = Stopwatch.StartNew();
        _phaseStopwatch = new Stopwatch();
    }

    public void StartPhase(int phaseNumber, string phaseName)
    {
        CurrentPhase = phaseNumber;
        _phaseStopwatch.Restart();
        _lastReportedItem = 0;

        _currentPhaseWeight = phaseNumber switch
        {
            1 => PHASE_PARSE,
            2 => PHASE_FETCH_TILES,
            3 => PHASE_IMPORT_GRIDS,
            4 => PHASE_ZOOM_LEVELS,
            5 => PHASE_MARKERS,
            _ => 0
        };
    }

    public void CompletePhase()
    {
        _completedPhaseWeight += _currentPhaseWeight;
    }

    public void Report(string phase, int current, int total, string? itemName = null, bool forceReport = false)
    {
        // Throttle reports to max once every 100ms unless forced or significant progress
        var now = DateTime.UtcNow;
        var timeSinceLastReport = (now - _lastReportTime).TotalMilliseconds;
        var itemsSinceLastReport = current - _lastReportedItem;

        // Report if: forced, first item, last item, 100ms passed, or 1% progress made
        var percentProgress = total > 0 ? (double)itemsSinceLastReport / total * 100 : 0;
        if (!forceReport && current != 1 && current != total && timeSinceLastReport < 100 && percentProgress < 1)
        {
            return;
        }

        _lastReportTime = now;
        _lastReportedItem = current;

        // Calculate phase progress (0-1)
        var phaseProgress = total > 0 ? (double)current / total : 0;

        // Calculate overall progress
        var overallPercent = _completedPhaseWeight + (_currentPhaseWeight * phaseProgress);

        // Calculate speed
        var elapsedSeconds = _stopwatch.Elapsed.TotalSeconds;
        var phaseElapsedSeconds = _phaseStopwatch.Elapsed.TotalSeconds;
        var itemsPerSecond = phaseElapsedSeconds > 0.5 ? current / phaseElapsedSeconds : 0;

        _progress?.Report(new HmapImportProgress
        {
            Phase = phase,
            CurrentItem = current,
            TotalItems = total,
            CurrentItemName = itemName ?? "",
            PhaseNumber = CurrentPhase,
            TotalPhases = TotalPhases,
            OverallPercent = Math.Min(overallPercent, 100),
            ElapsedSeconds = elapsedSeconds,
            ItemsPerSecond = Math.Round(itemsPerSecond, 1)
        });
    }
}

/// <summary>
/// Service for importing .hmap files into the map database
/// </summary>
public class HmapImportService : IHmapImportService
{
    private readonly IGridRepository _gridRepository;
    private readonly IMapRepository _mapRepository;
    private readonly ITileService _tileService;
    private readonly ITileRepository _tileRepository;
    private readonly IStorageQuotaService _quotaService;
    private readonly IMapNameService _mapNameService;
    private readonly IMarkerService _markerService;
    private readonly ILogger<HmapImportService> _logger;
    private const int GRID_SIZE = 100; // 100x100 tiles per grid

    public HmapImportService(
        IGridRepository gridRepository,
        IMapRepository mapRepository,
        ITileService tileService,
        ITileRepository tileRepository,
        IStorageQuotaService quotaService,
        IMapNameService mapNameService,
        IMarkerService markerService,
        ILogger<HmapImportService> logger)
    {
        _gridRepository = gridRepository;
        _mapRepository = mapRepository;
        _tileService = tileService;
        _tileRepository = tileRepository;
        _quotaService = quotaService;
        _mapNameService = mapNameService;
        _markerService = markerService;
        _logger = logger;
    }

    public async Task<HmapImportResult> ImportAsync(
        Stream hmapStream,
        string tenantId,
        HmapImportMode mode,
        string gridStorage,
        IProgress<HmapImportProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var result = new HmapImportResult();
        var tracker = new ImportProgressTracker(progress);

        try
        {
            // Phase 1: Parse .hmap file
            cancellationToken.ThrowIfCancellationRequested();
            tracker.StartPhase(1, "Parsing");
            tracker.Report("Parsing .hmap file", 0, 1, "Reading file...", forceReport: true);

            var reader = new HmapReader();
            var hmapData = reader.Read(hmapStream);
            tracker.Report("Parsing .hmap file", 1, 1, $"Found {hmapData.Grids.Count} grids", forceReport: true);
            tracker.CompletePhase();

            _logger.LogInformation("Parsed .hmap: {GridCount} grids, {SegmentCount} segments",
                hmapData.Grids.Count, hmapData.GetSegmentIds().Count());

            // Filter to only the 3 largest segments by grid count
            cancellationToken.ThrowIfCancellationRequested();
            var allSegments = hmapData.GetSegmentIds()
                .Select(id => new { Id = id, GridCount = hmapData.GetGridsForSegment(id).Count })
                .OrderByDescending(s => s.GridCount)
                .ToList();

            const int MAX_SEGMENTS = 3;
            var segments = allSegments.Take(MAX_SEGMENTS).Select(s => s.Id).ToList();
            var skippedSegments = allSegments.Skip(MAX_SEGMENTS).ToList();

            if (skippedSegments.Count > 0)
            {
                _logger.LogInformation(
                    "Skipping {SkippedCount} smaller segments (keeping top {MaxSegments} by grid count). Skipped: {SkippedDetails}",
                    skippedSegments.Count,
                    MAX_SEGMENTS,
                    string.Join(", ", skippedSegments.Select(s => $"{s.Id:X}({s.GridCount} grids)")));
            }

            // Get grids only for segments we're importing
            var gridsToImport = segments
                .SelectMany(id => hmapData.GetGridsForSegment(id))
                .ToList();

            _logger.LogInformation("Will import {GridCount} grids from {SegmentCount} segments",
                gridsToImport.Count, segments.Count);

            // Phase 2: Fetch tile resources from Haven server
            cancellationToken.ThrowIfCancellationRequested();
            var allResources = gridsToImport
                .SelectMany(g => g.Tilesets.Select(t => t.ResourceName))
                .Distinct()
                .ToList();

            tracker.StartPhase(2, "Fetching tiles");
            tracker.Report("Fetching tile resources", 0, allResources.Count, "Connecting to Haven...", forceReport: true);

            var tileCacheDir = Path.Combine(gridStorage, "hmap-tile-cache");
            using var tileResourceService = new TileResourceService(tileCacheDir);

            var fetchProgress = new Progress<(int current, int total, string name)>(p =>
            {
                tracker.Report("Fetching tile resources", p.current, p.total, p.name);
            });

            await tileResourceService.PrefetchTilesAsync(allResources, fetchProgress);
            tracker.CompletePhase();

            // Check for network errors during tile fetching
            var networkError = tileResourceService.GetFirstNetworkError();
            if (networkError != null)
            {
                _logger.LogWarning("Tile fetch warning: {NetworkError}", networkError);
            }

            // Phase 3: Import grids from each segment
            tracker.StartPhase(3, "Importing grids");
            var totalGridsToProcess = gridsToImport.Count;
            var processedGridsSoFar = 0;

            foreach (var segmentId in segments)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var segmentGrids = hmapData.GetGridsForSegment(segmentId);

                var (mapId, isNewMap, gridsImported, gridsSkipped, createdGridIds, gridsProcessed) = await ImportSegmentAsync(
                    segmentId, segmentGrids, tenantId, mode, gridStorage, tileResourceService,
                    tracker, processedGridsSoFar, totalGridsToProcess, cancellationToken);

                processedGridsSoFar += gridsProcessed;

                if (mapId > 0)
                {
                    result.AffectedMapIds.Add(mapId);
                    if (isNewMap)
                    {
                        result.CreatedMapIds.Add(mapId);
                    }
                    if (gridsImported > 0)
                        result.MapsCreated++;
                }

                result.CreatedGridIds.AddRange(createdGridIds);
                result.GridsImported += gridsImported;
                result.GridsSkipped += gridsSkipped;
                result.TilesRendered += gridsImported;
            }

            tracker.CompletePhase();

            // Phase 4: Generate zoom levels for affected maps
            tracker.StartPhase(4, "Generating zoom levels");
            var distinctMaps = result.AffectedMapIds.Distinct().ToList();
            var zoomIndex = 0;
            foreach (var mapId in distinctMaps)
            {
                cancellationToken.ThrowIfCancellationRequested();
                zoomIndex++;
                tracker.Report("Generating zoom levels", zoomIndex, distinctMaps.Count, $"Map {mapId}");
                await GenerateZoomLevelsForMapAsync(mapId, tenantId, gridStorage);
            }
            tracker.CompletePhase();

            // Phase 5: Import markers
            if (hmapData.Markers.Count > 0)
            {
                tracker.StartPhase(5, "Importing markers");
                var markerIndex = 0;
                var totalMarkers = hmapData.Markers.Count;

                foreach (var segmentId in segments)
                {
                    var segmentMarkers = hmapData.GetMarkersForSegment(segmentId);
                    var segmentGrids = hmapData.GetGridsForSegment(segmentId);

                    // Build lookup: (GridTileX, GridTileY) -> GridId
                    // Grid's TileX/TileY are the grid coordinates in world space
                    var gridLookup = segmentGrids.ToDictionary(
                        g => (g.TileX, g.TileY),
                        g => g.GridIdString
                    );

                    foreach (var marker in segmentMarkers)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        markerIndex++;
                        tracker.Report("Importing markers", markerIndex, totalMarkers, marker.Name);

                        // Convert marker's absolute tile coords to grid coords
                        // Marker TileX/TileY are absolute tile coordinates in world
                        var markerGridX = marker.TileX / GRID_SIZE;
                        var markerGridY = marker.TileY / GRID_SIZE;

                        // Find the grid this marker belongs to
                        if (!gridLookup.TryGetValue((markerGridX, markerGridY), out var gridId))
                        {
                            result.MarkersSkipped++;
                            continue; // No grid for this marker, skip
                        }

                        // Extract position within the grid (0-99)
                        var posX = marker.TileX % GRID_SIZE;
                        var posY = marker.TileY % GRID_SIZE;

                        // Determine image/icon based on marker type
                        var image = marker switch
                        {
                            HmapSMarker sm => sm.ResourceName,
                            _ => "gfx/terobjs/mm/custom"
                        };

                        try
                        {
                            // Create marker with correct sub-grid position
                            var markerData = new List<(string GridId, int X, int Y, string Name, string Image)>
                            {
                                (gridId, posX, posY, marker.Name, image)
                            };

                            await _markerService.BulkUploadMarkersAsync(markerData);
                            result.MarkersImported++;
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to import marker '{MarkerName}' at ({X},{Y})", marker.Name, posX, posY);
                            result.MarkersSkipped++;
                        }
                    }
                }

                _logger.LogInformation("Markers: {Imported} imported, {Skipped} skipped",
                    result.MarkersImported, result.MarkersSkipped);
                tracker.CompletePhase();
            }

            result.Success = true;
            stopwatch.Stop();
            result.Duration = stopwatch.Elapsed;

            _logger.LogInformation(
                "Import completed: {MapsCreated} maps, {GridsImported} grids imported, {GridsSkipped} skipped, {MarkersImported} markers, {Duration}ms",
                result.MapsCreated, result.GridsImported, result.GridsSkipped, result.MarkersImported, result.Duration.TotalMilliseconds);

            return result;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Import canceled for tenant {TenantId}", tenantId);
            result.Success = false;
            result.ErrorMessage = "Import was canceled";
            stopwatch.Stop();
            result.Duration = stopwatch.Elapsed;
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Import failed");
            result.Success = false;
            result.ErrorMessage = ex.Message;
            stopwatch.Stop();
            result.Duration = stopwatch.Elapsed;
            return result;
        }
    }

    public async Task CleanupFailedImportAsync(
        IEnumerable<int> mapIds,
        IEnumerable<string> gridIds,
        string tenantId,
        string gridStorage)
    {
        _logger.LogInformation("Cleaning up failed import for tenant {TenantId}: {MapCount} maps, {GridCount} grids",
            tenantId, mapIds.Count(), gridIds.Count());

        // Delete grids first (they may reference maps)
        foreach (var gridId in gridIds)
        {
            try
            {
                var grid = await _gridRepository.GetGridAsync(gridId);
                if (grid != null)
                {
                    await _gridRepository.DeleteGridAsync(gridId);
                    _logger.LogDebug("Deleted grid {GridId}", gridId);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to delete grid {GridId} during cleanup", gridId);
            }
        }

        // Delete maps (only newly created ones)
        foreach (var mapId in mapIds)
        {
            try
            {
                // Delete map directory (includes all tile files)
                var mapDir = Path.Combine(gridStorage, "tenants", tenantId, mapId.ToString());
                long totalDeletedBytes = 0;

                if (Directory.Exists(mapDir))
                {
                    // Calculate total size for storage quota adjustment
                    foreach (var file in Directory.GetFiles(mapDir, "*.png", SearchOption.AllDirectories))
                    {
                        try
                        {
                            totalDeletedBytes += new FileInfo(file).Length;
                        }
                        catch
                        {
                            // Ignore file access errors
                        }
                    }

                    try
                    {
                        Directory.Delete(mapDir, recursive: true);
                        _logger.LogDebug("Deleted map directory {MapDir}", mapDir);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to delete map directory {MapDir}", mapDir);
                    }
                }

                // Decrement storage quota
                if (totalDeletedBytes > 0)
                {
                    var sizeMB = totalDeletedBytes / (1024.0 * 1024.0);
                    await _quotaService.IncrementStorageUsageAsync(tenantId, -sizeMB);
                }

                // Delete all tile records for this map
                await _tileRepository.DeleteTilesByMapAsync(mapId);

                // Delete map record
                await _mapRepository.DeleteMapAsync(mapId);
                _logger.LogDebug("Deleted map {MapId}", mapId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to delete map {MapId} during cleanup", mapId);
            }
        }

        _logger.LogInformation("Cleanup completed for tenant {TenantId}", tenantId);
    }

    private async Task<(int mapId, bool isNewMap, int gridsImported, int gridsSkipped, List<string> createdGridIds, int gridsProcessed)> ImportSegmentAsync(
        long segmentId,
        List<HmapGridData> grids,
        string tenantId,
        HmapImportMode mode,
        string gridStorage,
        TileResourceService tileResourceService,
        ImportProgressTracker tracker,
        int processedSoFar,
        int totalGridsOverall,
        CancellationToken cancellationToken)
    {
        int mapId = 0;
        bool isNewMap = false;
        int gridsImported = 0;
        int gridsSkipped = 0;
        var createdGridIds = new List<string>();
        var segmentGridCount = grids.Count;

        // ===== STEP 1: Determine map and filter grids to import =====
        List<HmapGridData> gridsToImport;

        if (mode == HmapImportMode.CreateNew)
        {
            // Always create new map and import all grids
            mapId = await CreateNewMapAsync(tenantId);
            isNewMap = true;
            gridsToImport = grids;
            _logger.LogInformation("Created new map {MapId} for segment {SegmentId:X}", mapId, segmentId);
        }
        else // Merge mode
        {
            // Batch check for existing grids (single query instead of N queries)
            var allGridIds = grids.Select(g => g.GridIdString).ToList();
            var existingGridIds = await _gridRepository.GetExistingGridIdsAsync(allGridIds);

            // Find existing map from any existing grid
            int? existingMapId = null;
            if (existingGridIds.Count > 0)
            {
                var firstExistingId = existingGridIds.First();
                var existingGrid = await _gridRepository.GetGridAsync(firstExistingId);
                existingMapId = existingGrid?.Map;
            }

            if (existingMapId.HasValue)
            {
                mapId = existingMapId.Value;
                isNewMap = false;
                _logger.LogInformation("Merging segment {SegmentId:X} into existing map {MapId}", segmentId, mapId);
            }
            else
            {
                mapId = await CreateNewMapAsync(tenantId);
                isNewMap = true;
                _logger.LogInformation("Created new map {MapId} for segment {SegmentId:X} (no existing grids)", mapId, segmentId);
            }

            // Filter to only new grids
            gridsToImport = grids.Where(g => !existingGridIds.Contains(g.GridIdString)).ToList();
            gridsSkipped = grids.Count - gridsToImport.Count;

            if (gridsSkipped > 0)
            {
                _logger.LogInformation("Skipping {SkippedCount} existing grids in segment {SegmentId:X}",
                    gridsSkipped, segmentId);
            }
        }

        if (gridsToImport.Count == 0)
        {
            // Report progress for skipped grids
            tracker.Report("Importing grids", processedSoFar + segmentGridCount, totalGridsOverall,
                $"Segment {segmentId:X} - all grids exist", forceReport: true);
            return (mapId, isNewMap, 0, gridsSkipped, createdGridIds, segmentGridCount);
        }

        // ===== STEP 2: Producer-Consumer Pipeline =====
        // Producer: Parallel CPU rendering
        // Consumer: Sequential I/O and batched DB writes

        const int RENDER_PARALLELISM = 4; // CPU-bound rendering parallelism
        const int CHANNEL_CAPACITY = 20;  // Bounded buffer to limit memory
        const int BATCH_SIZE = 500;       // DB batch size

        var channel = Channel.CreateBounded<RenderedGrid>(new BoundedChannelOptions(CHANNEL_CAPACITY)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });

        var batchContext = new BatchImportContext(BATCH_SIZE);
        var processedCount = 0;
        var importedGridIds = new List<string>();
        Exception? producerException = null;

        // Producer task: Parallel rendering
        var producerTask = Task.Run(async () =>
        {
            using var semaphore = new SemaphoreSlim(RENDER_PARALLELISM);
            var renderTasks = new List<Task>();

            try
            {
                foreach (var grid in gridsToImport)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await semaphore.WaitAsync(cancellationToken);

                    var renderTask = Task.Run(async () =>
                    {
                        try
                        {
                            // Create grid data
                            var gridData = new GridData
                            {
                                Id = grid.GridIdString,
                                Map = mapId,
                                Coord = new Coord(grid.TileX, grid.TileY),
                                NextUpdate = DateTime.UtcNow.AddMinutes(-1),
                                TenantId = tenantId
                            };

                            // Compute paths
                            var relativePath = Path.Combine("tenants", tenantId, mapId.ToString(), "0",
                                $"{grid.TileX}_{grid.TileY}.png");
                            var fullPath = Path.Combine(gridStorage, relativePath);

                            // Render tile (CPU-bound)
                            var tileImage = await RenderGridTileAsync(grid, tileResourceService);

                            // Send to consumer
                            await channel.Writer.WriteAsync(
                                new RenderedGrid(grid, gridData, tileImage, relativePath, fullPath),
                                cancellationToken);
                        }
                        finally
                        {
                            semaphore.Release();
                        }
                    }, cancellationToken);

                    renderTasks.Add(renderTask);
                }

                await Task.WhenAll(renderTasks);
            }
            catch (Exception ex)
            {
                producerException = ex;
                throw;
            }
            finally
            {
                channel.Writer.Complete();
            }
        }, cancellationToken);

        // Consumer task: Sequential I/O and batched DB writes
        var consumerTask = Task.Run(async () =>
        {
            try
            {
                await foreach (var rendered in channel.Reader.ReadAllAsync(cancellationToken))
                {
                    try
                    {
                        // Ensure directory exists
                        var directory = Path.GetDirectoryName(rendered.FullPath)!;
                        if (!Directory.Exists(directory))
                            Directory.CreateDirectory(directory);

                        // Save tile to disk
                        await rendered.TileImage.SaveAsPngAsync(rendered.FullPath, cancellationToken);
                        var fileSize = (int)new FileInfo(rendered.FullPath).Length;

                        // Create tile data
                        var tileData = new TileData
                        {
                            MapId = mapId,
                            Coord = rendered.GridData.Coord,
                            Zoom = 0,
                            File = rendered.RelativePath,
                            Cache = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                            TenantId = tenantId,
                            FileSizeBytes = fileSize
                        };

                        // Add to batch
                        batchContext.AddGrid(rendered.GridData);
                        batchContext.AddTile(tileData);
                        batchContext.AddStorage(fileSize / (1024.0 * 1024.0));
                        importedGridIds.Add(rendered.GridData.Id);

                        // Report progress
                        processedCount++;
                        var overallProcessed = processedSoFar + gridsSkipped + processedCount;
                        tracker.Report(
                            "Importing grids",
                            overallProcessed,
                            totalGridsOverall,
                            $"Grid {rendered.SourceGrid.TileX},{rendered.SourceGrid.TileY}"
                        );

                        // Flush batch if needed
                        if (batchContext.ShouldFlush())
                        {
                            await FlushBatchAsync(batchContext);
                        }
                    }
                    finally
                    {
                        rendered.TileImage.Dispose();
                    }
                }

                // Flush remaining items
                if (batchContext.HasPendingItems)
                {
                    await FlushBatchAsync(batchContext);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Drain remaining items to dispose images
                while (channel.Reader.TryRead(out var item))
                {
                    item.TileImage.Dispose();
                }
                throw;
            }
        }, cancellationToken);

        // Wait for both tasks
        try
        {
            await Task.WhenAll(producerTask, consumerTask);
        }
        catch (Exception) when (producerException != null)
        {
            // Log the producer exception if it was the root cause
            _logger.LogError(producerException, "Producer task failed during import");
            throw;
        }

        gridsImported = processedCount;
        createdGridIds.AddRange(importedGridIds);

        // Clear memory cache periodically to prevent memory buildup
        tileResourceService.ClearMemoryCache();

        return (mapId, isNewMap, gridsImported, gridsSkipped, createdGridIds, segmentGridCount);
    }

    private async Task FlushBatchAsync(BatchImportContext batch)
    {
        var (grids, tiles, storageMB) = batch.ExtractBatch();

        if (grids.Count > 0)
        {
            await _gridRepository.SaveGridsBatchAsync(grids);
        }

        if (tiles.Count > 0)
        {
            await _tileRepository.SaveTilesBatchAsync(tiles);
        }

        if (storageMB > 0 && grids.Count > 0)
        {
            // Use the first grid's TenantId for quota update
            await _quotaService.IncrementStorageUsageAsync(grids[0].TenantId, storageMB);
        }

        _logger.LogDebug("Flushed batch: {GridCount} grids, {TileCount} tiles, {StorageMB:F2} MB",
            grids.Count, tiles.Count, storageMB);
    }

    private async Task<int> CreateNewMapAsync(string tenantId)
    {
        var mapName = await _mapNameService.GenerateUniqueIdentifierAsync(tenantId);

        var mapInfo = new MapInfo
        {
            Id = 0, // Let SQLite auto-generate
            Name = mapName,
            Hidden = false,
            Priority = 0,
            CreatedAt = DateTime.UtcNow,
            TenantId = tenantId
        };

        await _mapRepository.SaveMapAsync(mapInfo);
        return mapInfo.Id;
    }

    private async Task<Image<Rgba32>> RenderGridTileAsync(HmapGridData grid, TileResourceService tileResourceService)
    {
        var result = new Image<Rgba32>(GRID_SIZE, GRID_SIZE);

        // Load tile textures for this grid
        // GetTileImageAsync returns clones that we own and must dispose
        var tileTex = new Image<Rgba32>?[grid.Tilesets.Count];
        for (int i = 0; i < grid.Tilesets.Count; i++)
        {
            tileTex[i] = await tileResourceService.GetTileImageAsync(grid.Tilesets[i].ResourceName);
        }

        // ===== PASS 1: Base texture sampling =====
        for (int y = 0; y < GRID_SIZE; y++)
        {
            for (int x = 0; x < GRID_SIZE; x++)
            {
                var tileIndex = y * GRID_SIZE + x;
                if (grid.TileIndices == null || tileIndex >= grid.TileIndices.Length)
                {
                    result[x, y] = new Rgba32(128, 128, 128); // Gray for missing
                    continue;
                }

                var tsetIdx = grid.TileIndices[tileIndex];
                if (tsetIdx >= tileTex.Length || tileTex[tsetIdx] == null)
                {
                    result[x, y] = new Rgba32(128, 128, 128); // Gray for missing tile
                    continue;
                }

                var tex = tileTex[tsetIdx]!;
                // Sample from tile texture using floormod for proper wrapping
                var tx = ((x % tex.Width) + tex.Width) % tex.Width;
                var ty = ((y % tex.Height) + tex.Height) % tex.Height;
                result[x, y] = tex[tx, ty];
            }
        }

        // ===== PASS 2: Ridge/cliff shading =====
        // Check height differences and darken cliffs
        if (grid.ZMap != null && grid.TileIndices != null)
        {
            const float CLIFF_THRESHOLD = 2.0f;  // Height diff that triggers cliff detection
            const float EPSILON = 0.01f;

            for (int y = 1; y < GRID_SIZE - 1; y++)
            {
                for (int x = 1; x < GRID_SIZE - 1; x++)
                {
                    var idx = y * GRID_SIZE + x;

                    // Check height breaks with 4 cardinal neighbors
                    float z = grid.ZMap[idx];
                    bool broken = false;

                    // North
                    if (Math.Abs(z - grid.ZMap[(y - 1) * GRID_SIZE + x]) > CLIFF_THRESHOLD + EPSILON)
                        broken = true;
                    // South
                    if (!broken && Math.Abs(z - grid.ZMap[(y + 1) * GRID_SIZE + x]) > CLIFF_THRESHOLD + EPSILON)
                        broken = true;
                    // West
                    if (!broken && Math.Abs(z - grid.ZMap[y * GRID_SIZE + (x - 1)]) > CLIFF_THRESHOLD + EPSILON)
                        broken = true;
                    // East
                    if (!broken && Math.Abs(z - grid.ZMap[y * GRID_SIZE + (x + 1)]) > CLIFF_THRESHOLD + EPSILON)
                        broken = true;

                    if (broken)
                    {
                        // Darken 3x3 area around cliff
                        // Center pixel gets 100% black, neighbors get 10% darkening
                        for (int dy = -1; dy <= 1; dy++)
                        {
                            for (int dx = -1; dx <= 1; dx++)
                            {
                                var px = x + dx;
                                var py = y + dy;
                                var blend = (dx == 0 && dy == 0) ? 1.0f : 0.1f;
                                result[px, py] = BlendToBlack(result[px, py], blend);
                            }
                        }
                    }
                }
            }
        }

        // ===== PASS 3: Tile priority borders =====
        // Draw black borders where neighbor tiles have higher priority (tile ID)
        if (grid.TileIndices != null)
        {
            for (int y = 0; y < GRID_SIZE; y++)
            {
                for (int x = 0; x < GRID_SIZE; x++)
                {
                    var idx = y * GRID_SIZE + x;
                    var tileId = grid.TileIndices[idx];

                    // Check 4 neighbors for higher tile IDs
                    bool hasHigherNeighbor = false;

                    if (x > 0 && grid.TileIndices[idx - 1] > tileId) hasHigherNeighbor = true;
                    if (!hasHigherNeighbor && x < GRID_SIZE - 1 && grid.TileIndices[idx + 1] > tileId) hasHigherNeighbor = true;
                    if (!hasHigherNeighbor && y > 0 && grid.TileIndices[idx - GRID_SIZE] > tileId) hasHigherNeighbor = true;
                    if (!hasHigherNeighbor && y < GRID_SIZE - 1 && grid.TileIndices[idx + GRID_SIZE] > tileId) hasHigherNeighbor = true;

                    if (hasHigherNeighbor)
                        result[x, y] = new Rgba32(0, 0, 0, 255);  // Black border
                }
            }
        }

        // Dispose tile textures (they are clones we own)
        foreach (var img in tileTex)
        {
            img?.Dispose();
        }

        return result;
    }

    /// <summary>
    /// Blend a color toward black by the specified factor (0.0 = no change, 1.0 = pure black)
    /// </summary>
    private static Rgba32 BlendToBlack(Rgba32 color, float factor)
    {
        var f1 = (int)(factor * 255);
        var f2 = 255 - f1;
        return new Rgba32(
            (byte)((color.R * f2) / 255),
            (byte)((color.G * f2) / 255),
            (byte)((color.B * f2) / 255),
            color.A
        );
    }

    private async Task GenerateZoomLevelsForMapAsync(int mapId, string tenantId, string gridStorage)
    {
        // Get all zoom-0 tiles for this map
        var grids = await _gridRepository.GetGridsByMapAsync(mapId);
        if (grids.Count == 0)
            return;

        // Build set of coordinates that need zoom regeneration
        var coordsToProcess = new HashSet<(int zoom, Coord coord)>();

        foreach (var grid in grids)
        {
            var coord = grid.Coord;
            for (int zoom = 1; zoom <= 6; zoom++)
            {
                coord = coord.Parent();
                coordsToProcess.Add((zoom, coord));
            }
        }

        // Process zoom levels in order (1 depends on 0, 2 depends on 1, etc.)
        // Sequential processing required because:
        // 1. DbContext is not thread-safe
        // 2. SQLite has limited concurrent write support
        for (int zoom = 1; zoom <= 6; zoom++)
        {
            var zoomCoords = coordsToProcess
                .Where(c => c.zoom == zoom)
                .Select(c => c.coord)
                .Distinct()
                .ToList();

            foreach (var coord in zoomCoords)
            {
                try
                {
                    await _tileService.UpdateZoomLevelAsync(mapId, coord, zoom, tenantId, gridStorage);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to generate zoom {Zoom} for {Coord}", zoom, coord);
                }
            }
        }
    }
}
