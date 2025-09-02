using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Mime;
using Jellyfin.Data.Entities;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Jellio.Helpers;
using Jellyfin.Plugin.Jellio.Models;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Common.Extensions;
using MediaBrowser.Model.MediaInfo;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Jellio.Controllers;

[ApiController]
[ConfigAuthorize]
[Route("jellio/{config}")]
[Produces(MediaTypeNames.Application.Json)]
public class AddonController(
    IUserViewManager userViewManager,
    IDtoService dtoService,
    ILibraryManager libraryManager,
    ISessionManager sessionManager
) : ControllerBase
{
    private string GetBaseUrl()
    {
        return $"{Request.Scheme}://{Request.Host}{Request.PathBase}";
    }

    private static MetaDto MapToMeta(
        BaseItemDto dto,
        StremioType stremioType,
        string baseUrl,
        bool includeDetails = false
    )
    {
        string? releaseInfo = null;
        if (dto.PremiereDate.HasValue)
        {
            var premiereYear = dto.PremiereDate.Value.Year.ToString(CultureInfo.InvariantCulture);
            releaseInfo = premiereYear;
            if (stremioType == StremioType.Series)
            {
                releaseInfo += "-";
                if (dto.Status != "Continuing" && dto.EndDate.HasValue)
                {
                    var endYear = dto.EndDate.Value.Year.ToString(CultureInfo.InvariantCulture);
                    if (premiereYear != endYear)
                    {
                        releaseInfo += endYear;
                    }
                }
            }
        }

        var meta = new MetaDto
        {
            Id = dto.ProviderIds.TryGetValue("Imdb", out var idVal) ? idVal : $"jellio:{dto.Id}",
            Type = stremioType.ToString().ToLower(CultureInfo.InvariantCulture),
            Name = dto.Name,
            Poster = $"{baseUrl}/Items/{dto.Id}/Images/Primary",
            PosterShape = "poster",
            Genres = dto.Genres,
            Description = dto.Overview,
            ImdbRating = dto.CommunityRating?.ToString("F1", CultureInfo.InvariantCulture),
            ReleaseInfo = releaseInfo,
        };

        if (includeDetails)
        {
            meta.Runtime =
                dto.RunTimeTicks.HasValue && dto.RunTimeTicks.Value != 0
                    ? $"{dto.RunTimeTicks.Value / 600000000} min"
                    : null;
            meta.Logo = dto.ImageTags.ContainsKey(ImageType.Logo)
                ? $"{baseUrl}/Items/{dto.Id}/Images/Logo"
                : null;
            meta.Background =
                dto.BackdropImageTags.Length != 0
                    ? $"{baseUrl}/Items/{dto.Id}/Images/Backdrop/0"
                    : null;
            meta.Released = dto.PremiereDate?.ToString("o");
        }

        return meta;
    }

    private OkObjectResult GetStreamsResult(User user, List<BaseItem> items)
    {
        var baseUrl = GetBaseUrl();
        var dtoOptions = new DtoOptions(true);
        var dtos = dtoService.GetBaseItemDtos(items, dtoOptions, user);
        var streams = dtos.SelectMany(dto =>
            dto.MediaSources.Select(source => new StreamDto
            {
                Url = $"{baseUrl}/videos/{dto.Id}/stream?mediaSourceId={source.Id}&static=true",
                Name = FormatStreamNameWithEmojis(dto, source),
                Description = FormatStreamDescriptionWithEmojis(dto, source),
            })
        );

        // Report playback to Jellyfin Active Devices
        if (items.Count > 0)
        {
            ReportPlaybackToJellyfin(user, items[0]);
        }

        return Ok(new { streams });
    }

    private static string FormatStreamNameWithEmojis(BaseItemDto dto, MediaSourceInfo source)
    {
        var parts = new List<string>();

        // Determine video source type (BluRay REMUX, etc.)
        var sourceType = "BluRay REMUX"; // Default, could be enhanced based on filename or codec analysis
        parts.Add($"🎥 {sourceType}");

        // 📺 Video quality info (HDR, DV, etc.)
        if (source.MediaStreams != null)
        {
            var videoStream = source.MediaStreams.FirstOrDefault(s => s.Type == MediaStreamType.Video);
            if (videoStream != null)
            {
                var videoInfo = new List<string>();

                // Add HDR info
                if (!string.IsNullOrEmpty(videoStream.ColorTransfer) &&
                    (videoStream.ColorTransfer.Contains("2020", StringComparison.OrdinalIgnoreCase) ||
                     videoStream.ColorTransfer.Contains("2100", StringComparison.OrdinalIgnoreCase)))
                {
                    videoInfo.Add("HDR");
                }
                else if (!string.IsNullOrEmpty(videoStream.ColorSpace) &&
                         videoStream.ColorSpace.Contains("2020", StringComparison.OrdinalIgnoreCase))
                {
                    videoInfo.Add("HDR");
                }

                // Add Dolby Vision detection
                if (!string.IsNullOrEmpty(videoStream.ColorTransfer) &&
                    videoStream.ColorTransfer.Contains("2084", StringComparison.OrdinalIgnoreCase))
                {
                    videoInfo.Add("DV");
                }

                if (videoInfo.Count > 0)
                {
                    parts.Add($"📺 {string.Join(" ", videoInfo)}");
                }
            }
        }

        return string.Join("\n", parts);
    }

    private static string FormatStreamDescriptionWithEmojis(BaseItemDto dto, MediaSourceInfo source)
    {
        var parts = new List<string>();

        // 📦 File size
        if (source.Size.HasValue && source.Size.Value > 0)
        {
            var sizeInGB = source.Size.Value / (1024.0 * 1024.0 * 1024.0);
            parts.Add($"📦 {sizeInGB:F2} GB");
        }

        // 📁 Filename (use source name or dto name as fallback)
        var filename = !string.IsNullOrEmpty(source.Name) && source.Name != dto.Name
            ? source.Name
            : $"{dto.Name}.mkv"; // Default extension
        parts.Add($"📁 {filename}");

        // Footer
        parts.Add("[JF] Jellio 4K");

        return parts.Count > 0 ? string.Join("\n", parts) : "Jellio Stream";
    }

    private void ReportPlaybackToJellyfin(User user, BaseItem item)
    {
        try
        {
            Console.WriteLine($"=== Starting playback report for user {user.Username} ===");

            // Find the Jellio session for this user
            var sessions = sessionManager.Sessions.Where(s => s.UserId == user.Id && s.DeviceName == "Jellio").ToList();
            Console.WriteLine($"Found {sessions.Count} existing Jellio sessions for user {user.Username}");

            if (sessions.Count > 0)
            {
                var session = sessions[0];
                Console.WriteLine($"Using existing session: {session.Id} for device: {session.DeviceId}");

                // Keep the session active by logging activity
                // The session is already active, so Jellyfin will show the user as online
                Console.WriteLine($"Session active for user {user.Username}: {item.Name} (ID: {item.Id})");

                // Note: We can't update the session with playback info due to API limitations
                // But the session remains active, which is what we need for Active Devices visibility
            }
            else
            {
                Console.WriteLine($"No existing Jellio session found for user {user.Username}, creating new one...");

                // If no session exists, we need to create one to show up in Active Devices
                // We'll use the same approach as AuthController.StartSession()
                try
                {
                    var deviceId = Guid.NewGuid().ToString();
                    Console.WriteLine($"Creating new device with ID: {deviceId}");

                    var authenticationResult = sessionManager.AuthenticateDirect(
                        new AuthenticationRequest
                        {
                            UserId = user.Id,
                            DeviceId = deviceId,
                            DeviceName = "Jellio",
                            App = "Jellio",
                            AppVersion = "1.0.0",
                        }
                    ).ConfigureAwait(false).GetAwaiter().GetResult();

                    Console.WriteLine($"Successfully created new session for user {user.Username}: {item.Name} (ID: {item.Id})");
                    Console.WriteLine($"New access token: {authenticationResult.AccessToken}");

                    // Verify the session was created
                    var newSessions = sessionManager.Sessions.Where(s => s.UserId == user.Id && s.DeviceName == "Jellio").ToList();
                    Console.WriteLine($"After creation, found {newSessions.Count} Jellio sessions for user {user.Username}");
                }
                catch (Exception authEx)
                {
                    Console.WriteLine($"Failed to create session for user {user.Username}: {authEx.Message}");
                    Console.WriteLine($"Exception details: {authEx}");
                }
            }

            Console.WriteLine($"=== Completed playback report for user {user.Username} ===");
        }
        catch (Exception ex)
        {
            // Log error but don't fail the request
            Console.WriteLine($"Failed to report playback to Jellyfin: {ex.Message}");
            Console.WriteLine($"Exception details: {ex}");
        }
    }

    private static string BuildStreamName(BaseItemDto dto, MediaSourceInfo source)
    {
        var parts = new List<string>();

        // Add movie/series title with year
        var title = dto.Name;
        if (dto.PremiereDate.HasValue)
        {
            title += $" ({dto.PremiereDate.Value.Year})";
        }
        parts.Add(title);

        // Add video quality info
        if (source.MediaStreams != null)
        {
            var videoStream = source.MediaStreams.FirstOrDefault(s => s.Type == MediaStreamType.Video);
            if (videoStream != null)
            {
                var videoInfo = new List<string>();

                // Add resolution
                if (videoStream.Width.HasValue && videoStream.Height.HasValue)
                {
                    videoInfo.Add(GetHumanReadableResolution(videoStream.Width.Value, videoStream.Height.Value));
                }

                // Add HDR info
                if (!string.IsNullOrEmpty(videoStream.ColorTransfer) &&
                    (videoStream.ColorTransfer.Contains("2020", StringComparison.OrdinalIgnoreCase) ||
                     videoStream.ColorTransfer.Contains("2100", StringComparison.OrdinalIgnoreCase)))
                {
                    videoInfo.Add("HDR");
                }
                else if (!string.IsNullOrEmpty(videoStream.ColorSpace) &&
                         videoStream.ColorSpace.Contains("2020", StringComparison.OrdinalIgnoreCase))
                {
                    videoInfo.Add("HDR");
                }

                // Add Dolby Vision detection
                if (!string.IsNullOrEmpty(videoStream.ColorTransfer) &&
                    videoStream.ColorTransfer.Contains("2084", StringComparison.OrdinalIgnoreCase))
                {
                    videoInfo.Add("DV");
                }

                // Add codec info
                if (!string.IsNullOrEmpty(videoStream.Codec))
                {
                    videoInfo.Add(videoStream.Codec.ToUpperInvariant());
                }

                if (videoInfo.Count > 0)
                {
                    parts.Add(string.Join(" | ", videoInfo));
                }
            }
        }

        return string.Join("\n", parts);
    }

    private static string BuildStreamDescription(BaseItemDto dto, MediaSourceInfo source)
    {
        var parts = new List<string>();

        // Add source name if different from title
        if (!string.IsNullOrEmpty(source.Name) && source.Name != dto.Name)
        {
            parts.Add(source.Name);
        }

        // Add audio language info
        if (source.MediaStreams != null)
        {
            var audioStreams = source.MediaStreams.Where(s => s.Type == MediaStreamType.Audio).ToList();
            if (audioStreams.Count > 0)
            {
                var languages = audioStreams
                    .Where(s => !string.IsNullOrEmpty(s.Language))
                    .Select(s => s.Language)
                    .Distinct()
                    .ToList();

                if (languages.Count > 0)
                {
                    parts.Add($"Audio: {string.Join(", ", languages)}");
                }

                // Add audio codec info
                var audioCodecs = audioStreams
                    .Where(s => !string.IsNullOrEmpty(s.Codec))
                    .Select(s => s.Codec.ToUpperInvariant())
                    .Distinct()
                    .ToList();

                if (audioCodecs.Count > 0)
                {
                    parts.Add($"Codec: {string.Join(", ", audioCodecs)}");
                }
            }
        }

        // Add file size if available
        if (source.Size.HasValue && source.Size.Value > 0)
        {
            var sizeInGB = source.Size.Value / (1024.0 * 1024.0 * 1024.0);
            parts.Add($"Size: {sizeInGB:F1} GB");
        }

        // Add resolution to footer
        if (source.MediaStreams != null)
        {
            var videoStream = source.MediaStreams.FirstOrDefault(s => s.Type == MediaStreamType.Video);
            if (videoStream != null && videoStream.Width.HasValue && videoStream.Height.HasValue)
            {
                var resolution = GetHumanReadableResolution(videoStream.Width.Value, videoStream.Height.Value);
                parts.Add($"Jellyfin {resolution}");
            }
        }

        return parts.Count > 0 ? string.Join(" | ", parts) : "Jellio Stream";
    }

    private static string GetHumanReadableResolution(int width, int height)
    {
        return (width, height) switch
        {
            (7680, 4320) => "8K",
            (3840, 2160) => "4K",
            (3840, 2076) => "4K", // Common 4K variation
            (2560, 1440) => "1440p",
            (1920, 1080) => "1080p",
            (1280, 720) => "720p",
            (854, 480) => "480p",
            (640, 360) => "360p",
            (426, 240) => "240p",
            _ => width switch
            {
                >= 7680 => "8K+",
                >= 3840 => "4K",
                >= 2560 => "1440p",
                >= 1920 => "1080p",
                >= 1280 => "720p",
                >= 854 => "480p",
                >= 640 => "360p",
                >= 426 => "240p",
                _ => $"{width}x{height}"
            }
        };
    }

    [HttpGet("manifest.json")]
    public IActionResult GetManifest([ConfigFromBase64Json] ConfigModel config)
    {
        var user = (User)HttpContext.Items["JellioUser"]!;

        var userLibraries = LibraryHelper.GetUserLibraries(user, userViewManager, dtoService);
        userLibraries = Array.FindAll(userLibraries, l => config.LibrariesGuids.Contains(l.Id));
        if (userLibraries.Length != config.LibrariesGuids.Count)
        {
            return NotFound();
        }

        var catalogs = userLibraries.Select(lib =>
        {
            return new
            {
                type = lib.CollectionType switch
                {
                    CollectionType.movies => "movie",
                    CollectionType.tvshows => "series",
                    _ => null,
                },
                id = lib.Id.ToString(),
                name = $"{lib.Name} | {config.ServerName}",
                extra = new[]
                {
                    new { name = "skip", isRequired = false },
                    new { name = "search", isRequired = false },
                },
            };
        });

        var catalogNames = userLibraries.Select(l => l.Name).ToList();
        var descriptionText = $"Play movies and series from {config.ServerName}: {string.Join(", ", catalogNames)}";
        var manifest = new
        {
            id = "com.stremio.jellio",
            version = "0.0.1",
            name = "Jellio",
            description = descriptionText,
            resources = new object[]
            {
                "catalog",
                "stream",
                new
                {
                    name = "meta",
                    types = new[] { "movie", "series" },
                    idPrefixes = new[] { "jellio" },
                },
            },
            types = new[] { "movie", "series" },
            idPrefixes = new[] { "tt", "jellio" },
            contactEmail = "support@jellio.stream",
            behaviorHints = new { configurable = true },
            catalogs,
        };

        return Ok(manifest);
    }

    [HttpGet("catalog/{stremioType}/{catalogId:guid}/{extra}.json")]
    [HttpGet("catalog/{stremioType}/{catalogId:guid}.json")]
    public IActionResult GetCatalog(
        [ConfigFromBase64Json] ConfigModel config,
        StremioType stremioType,
        Guid catalogId,
        string? extra = null
    )
    {
        var user = (User)HttpContext.Items["JellioUser"]!;

        var userLibraries = LibraryHelper.GetUserLibraries(user, userViewManager, dtoService);
        var catalogLibrary = Array.Find(userLibraries, l => l.Id == catalogId);
        if (catalogLibrary == null)
        {
            return NotFound();
        }

        var item = libraryManager.GetParentItem(catalogLibrary.Id, user.Id);
        if (item is not Folder folder)
        {
            folder = libraryManager.GetUserRootFolder();
        }

        var extras =
            extra
                ?.Split('&')
                .Select(e => e.Split('='))
                .Where(parts => parts.Length == 2)
                .ToDictionary(parts => parts[0], parts => parts[1])
            ?? new Dictionary<string, string>();

        int startIndex =
            extras.TryGetValue("skip", out var skipValue)
            && int.TryParse(skipValue, out var parsedSkip)
                ? parsedSkip
                : 0;
        extras.TryGetValue("search", out var searchTerm);

        var dtoOptions = new DtoOptions
        {
            Fields = [ItemFields.ProviderIds, ItemFields.Overview, ItemFields.Genres],
        };
        var query = new InternalItemsQuery(user)
        {
            Recursive = true, // need this for search to work
            IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Series],
            OrderBy =
            [
                (ItemSortBy.ProductionYear, SortOrder.Descending),
                (ItemSortBy.SortName, SortOrder.Ascending),
            ],
            Limit = 100,
            StartIndex = startIndex,
            SearchTerm = searchTerm,
            ParentId = catalogLibrary.Id,
            DtoOptions = dtoOptions,
        };
        var result = folder.GetItems(query);
        var dtos = dtoService.GetBaseItemDtos(result.Items, dtoOptions, user);
        var baseUrl = GetBaseUrl();
        var metas = dtos.Select(dto => MapToMeta(dto, stremioType, baseUrl));

        return Ok(new { metas });
    }

    [HttpGet("meta/{stremioType}/jellio:{mediaId:guid}.json")]
    public IActionResult GetMeta(
        [ConfigFromBase64Json] ConfigModel config,
        StremioType stremioType,
        Guid mediaId
    )
    {
        var user = (User)HttpContext.Items["JellioUser"]!;

        var item = libraryManager.GetItemById<BaseItem>(mediaId, user);
        if (item == null)
        {
            return NotFound();
        }

        var dtoOptions = new DtoOptions
        {
            Fields = [ItemFields.ProviderIds, ItemFields.Overview, ItemFields.Genres],
        };
        var dto = dtoService.GetBaseItemDto(item, dtoOptions, user);
        var baseUrl = GetBaseUrl();
        var meta = MapToMeta(dto, stremioType, baseUrl, includeDetails: true);

        if (stremioType is StremioType.Series)
        {
            if (item is not Series series)
            {
                return BadRequest();
            }

            var episodes = series.GetEpisodes(user, dtoOptions, false).ToList();
            var seriesItemOptions = new DtoOptions { Fields = [ItemFields.Overview] };
            var dtos = dtoService.GetBaseItemDtos(episodes, seriesItemOptions, user);
            var videos = dtos.Select(episode => new VideoDto
            {
                Id = $"jellio:{episode.Id}",
                Title = episode.Name,
                Thumbnail = $"{baseUrl}/Items/{episode.Id}/Images/Primary",
                Available = true,
                Episode = episode.IndexNumber ?? 0,
                Season = episode.ParentIndexNumber ?? 0,
                Overview = episode.Overview,
                Released = episode.PremiereDate?.ToString("o"),
            });
            meta.Videos = videos;
        }

        return Ok(new { meta });
    }

    [HttpGet("stream/{stremioType}/jellio:{mediaId:guid}.json")]
    public IActionResult GetStream(
        [ConfigFromBase64Json] ConfigModel config,
        StremioType stremioType,
        Guid mediaId
    )
    {
        var user = (User)HttpContext.Items["JellioUser"]!;

        var item = libraryManager.GetItemById<BaseItem>(mediaId, user);
        if (item == null)
        {
            return NotFound();
        }

        return GetStreamsResult(user, [item]);
    }

    [HttpGet("stream/movie/tt{imdbId}.json")]
    public IActionResult GetStreamImdbMovie(
        [ConfigFromBase64Json] ConfigModel config,
        string imdbId
    )
    {
        var user = (User)HttpContext.Items["JellioUser"]!;

        var query = new InternalItemsQuery(user)
        {
            HasAnyProviderId = new Dictionary<string, string> { ["Imdb"] = $"tt{imdbId}" },
            IncludeItemTypes = [BaseItemKind.Movie],
        };
        var items = libraryManager.GetItemList(query);

        return GetStreamsResult(user, items);
    }

    [HttpGet("stream/series/tt{imdbId}:{seasonNum:int}:{episodeNum:int}.json")]
    public IActionResult GetStreamImdbTv(
        [ConfigFromBase64Json] ConfigModel config,
        string imdbId,
        int seasonNum,
        int episodeNum
    )
    {
        var user = (User)HttpContext.Items["JellioUser"]!;

        var seriesQuery = new InternalItemsQuery(user)
        {
            IncludeItemTypes = [BaseItemKind.Series],
            HasAnyProviderId = new Dictionary<string, string> { ["Imdb"] = $"tt{imdbId}" },
        };
        var seriesItems = libraryManager.GetItemList(seriesQuery);

        if (seriesItems.Count == 0)
        {
            return NotFound();
        }

        var seriesIds = seriesItems.Select(s => s.Id).ToArray();

        var episodeQuery = new InternalItemsQuery(user)
        {
            IncludeItemTypes = [BaseItemKind.Episode],
            AncestorIds = seriesIds,
            ParentIndexNumber = seasonNum,
            IndexNumber = episodeNum,
        };
        var episodeItems = libraryManager.GetItemList(episodeQuery);

        return GetStreamsResult(user, episodeItems);
    }

    [HttpPost("playback/progress")]
    public IActionResult ReportPlaybackProgress(
        [ConfigFromBase64Json] ConfigModel config,
        [FromBody] PlaybackProgressRequest request
    )
    {
        var user = (User)HttpContext.Items["JellioUser"]!;

        try
        {
            // Parse the itemId from "jellio:guid" format
            if (!request.ItemId.StartsWith("jellio:", StringComparison.OrdinalIgnoreCase))
            {
                return BadRequest(new { error = "Invalid itemId format. Expected 'jellio:guid'" });
            }

            var guidString = request.ItemId.Substring(7); // Remove "jellio:" prefix
            if (!Guid.TryParse(guidString, out var itemGuid))
            {
                return BadRequest(new { error = "Invalid GUID format in itemId" });
            }

            var item = libraryManager.GetItemById<BaseItem>(itemGuid, user);
            if (item == null)
            {
                return NotFound();
            }

            UpdatePlaybackProgress(user, item, request.PositionTicks, request.IsPaused);
            return Ok();
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("playback/stop")]
    public IActionResult ReportPlaybackStop(
        [ConfigFromBase64Json] ConfigModel config,
        [FromBody] PlaybackStopRequest request
    )
    {
        var user = (User)HttpContext.Items["JellioUser"]!;

        try
        {
            // Parse the itemId from "jellio:guid" format
            if (!request.ItemId.StartsWith("jellio:", StringComparison.OrdinalIgnoreCase))
            {
                return BadRequest(new { error = "Invalid itemId format. Expected 'jellio:guid'" });
            }

            var guidString = request.ItemId.Substring(7); // Remove "jellio:" prefix
            if (!Guid.TryParse(guidString, out var itemGuid))
            {
                return BadRequest(new { error = "Invalid GUID format in itemId" });
            }

            var item = libraryManager.GetItemById<BaseItem>(itemGuid, user);
            if (item == null)
            {
                return NotFound();
            }

            StopPlayback(user, item, request.PositionTicks);
            return Ok();
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    private void UpdatePlaybackProgress(User user, BaseItem item, long positionTicks, bool isPaused)
    {
        try
        {
            var sessions = sessionManager.Sessions.Where(s => s.UserId == user.Id && s.DeviceName == "Jellio").ToList();

            if (sessions.Count > 0)
            {
                var session = sessions[0];

                // Log playback progress for now
                // The session remains active, which keeps the user visible in Active Devices
                var positionSeconds = positionTicks / 10000000; // Convert ticks to seconds
                Console.WriteLine($"Playback progress for {user.Username}: {item.Name} at {positionSeconds}s (Paused: {isPaused})");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to update playback progress: {ex.Message}");
        }
    }

    private void StopPlayback(User user, BaseItem item, long positionTicks)
    {
        try
        {
            var sessions = sessionManager.Sessions.Where(s => s.UserId == user.Id && s.DeviceName == "Jellio").ToList();

            if (sessions.Count > 0)
            {
                var session = sessions[0];

                // Log playback stop for now
                // The session remains active, which keeps the user visible in Active Devices
                var positionSeconds = positionTicks / 10000000; // Convert ticks to seconds
                Console.WriteLine($"Playback stopped for {user.Username}: {item.Name} at {positionSeconds}s");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to stop playback: {ex.Message}");
        }
    }
}
