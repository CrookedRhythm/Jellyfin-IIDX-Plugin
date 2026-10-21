# Jellyfin beatmania IIDX plugin

Plays beatmania IIDX songs in Jellyfin with their background movies, straight from the game data.
Every song in the game's music database shows up in a dedicated **beatmania IIDX** library with its
title, artist, genre, style, levels and BPM, so Jellyfin's normal sorting, filters and search work on
it. When you press play, the plugin renders the song from the original chart and keysounds, muxes it
with the song's movie, and hands it to Jellyfin. Renders expire after a while and the space is freed
again. **Game files are only ever read; nothing is converted permanently.**

Supported game data:

| What | Formats |
| --- | --- |
| Music database | `music_data.bin` data versions 20 (tricoro) to 33 (Sparkle Shower) and INFINITAS (80). Newer versions are read with the latest known layout; the entry size is measured from the file, so versions that append fields still parse. The game keeps the previous file in `data/info/0` or `data/info/1`; the newer one (data version, then song count, then file date) is used. |
| Charts | `.1` (SP/DP BEGINNER, NORMAL, HYPER, ANOTHER, LEGGENDARIA) |
| Keysounds | `.s3p` (WMA), `.2dx` (MS-ADPCM or PCM WAV) |
| Packaging | per-song `data/sound/<id>.ifs` archives or extracted `data/sound/<id>/` folders; `data_mods/*` (omnimix etc.) overrides `data` |
| Movies | `data/movie/<bga_filename>.mp4/.wmv/...`, loose or inside `.ifs` archives under `data/movie` (read in place, never extracted) |

Tested against Jellyfin **12.2.0** (Docker image `jellyfin/jellyfin:latest`).

## How it works

1. **Index** (scheduled task *Update beatmania IIDX library*, runs at startup and daily). Reads the
   music database and, for every song with chart data, writes a folder into the library containing
   an NFO file with the metadata, a thumbnail grabbed from the movie, and a silent placeholder file
   (about 30 KB). It then creates the Jellyfin library if it does not exist yet.
2. **Play**. Before Jellyfin builds playback info it re-probes any video item that has no video
   stream, which is the case for placeholders. The plugin hooks that moment: it renders the song
   (auto-play of the chosen chart: every note's keysound plus the background keysounds), muxes it
   over the movie, and swaps the file in. Jellyfin then probes the real file and serves it like any
   other video, so direct play, remuxing and transcoding work in every client.
3. **Expire**. Renders not played for the configured time (default 6 hours), or beyond the cache size
   limit (default 4 GB), are swapped back to placeholders.

On a 4 core machine a 2 minute song with 800 WMA keysounds and a 1080p H.264 movie renders in about
5 seconds; songs with fewer keysounds take one or two. H.264/HEVC movies are copied without
re-encoding. WMV movies are copied into MKV and Jellyfin converts them while streaming, so they start
just as fast.

### Metadata mapping

| Jellyfin field | From |
| --- | --- |
| Title / sort title | title / ASCII title (so the A–Z sort follows the in-game English names) |
| Artist, Genre | artist, genre |
| Album, Studio, Year | debut style, e.g. *beatmania IIDX 17 SIRIUS*, 2009 |
| Community rating | SP ANOTHER level by default (configurable), so you can sort by difficulty |
| Tags | `SP ☆12`, `DP ☆11`, `LEGGENDARIA`, `BEGINNER`, `BPM 150-159`, `BPM change`, `No movie` |
| Overview | levels of every chart, BPM, license credits |

## Install

1. Build the plugin (or download the zip from the Releases page):
   ```
   dotnet publish src/Jellyfin.Plugin.IIDX -c Release -o publish
   ```
2. Copy `Jellyfin.Plugin.IIDX.dll` and `IIDX.Core.dll` into
   `<jellyfin config>/plugins/beatmania IIDX_1.0.0.0/` and restart Jellyfin.
3. Make the game data visible to the container, read-only is fine:
   ```yaml
   services:
     jellyfin:
       image: jellyfin/jellyfin:latest
       volumes:
         - /path/to/config:/config
         - /path/to/cache:/cache
         - /path/to/iidx/contents:/iidx:ro
   ```
4. In **Dashboard → Plugins → beatmania IIDX**, set *Game data folder* to `/iidx` (the folder holding
   `data`), press *Check game data*, then *Save and update library now*.

The library (metadata, thumbnails, placeholders and current renders) lives in
`/config/data/iidx/library` unless you choose another folder.

### Keeping renders in RAM

Renders are ordinary files in the library folder, so they can live in RAM by putting that folder on a
tmpfs, e.g. `--mount type=tmpfs,destination=/iidx-library,tmpfs-size=4g` and setting *Library folder*
to `/iidx-library`. The index task runs at startup and rebuilds the folder after a restart. Keep the
cache size limit below the tmpfs size.

## Settings

| Setting | Default | |
| --- | --- | --- |
| Chart to render | `SPA,SPH,SPN,SPL,SPB,DPA,...` | first chart that exists is rendered |
| Audio | FLAC | lossless and fastest; Jellyfin converts for clients without FLAC. AAC is also available. |
| Copy H.264/HEVC movies | on | otherwise movies are re-encoded with x264 at the given CRF |
| Loop short movies | on | |
| Include background keysounds | on | off gives just the note sounds |
| Cut retriggered keysounds | on | a keysound played again stops its previous instance |
| bga_delay unit | milliseconds | the unit of the music db's movie offset is not documented; switch to frames if movies look out of sync |
| Keep renders for | 360 minutes | |
| Cache size limit | 4096 MB | 0 = unlimited |

## Command line tool

`src/IIDX.Cli` builds `iidx-tool`, which runs the same code without Jellyfin:

```
iidx-tool db      --music-data music_data.bin [--json]
iidx-tool inspect --game /path/to/contents
iidx-tool index   --game /path/to/contents --library ./lib --state ./state
iidx-tool render  --game /path/to/contents --library ./lib --state ./state --id 1000,1001
```

## Development

```
dotnet test tests/IIDX.Core.Tests
python3 tools/make_test_gamedata.py /tmp/fake-contents   # needs ffmpeg and `pip install ifstools`
```

`tools/make_test_gamedata.py` builds a small fake `contents` folder (generated tones and test
patterns, no game content) covering `.ifs` + `.s3p`, `.ifs` + MS-ADPCM `.2dx`, loose files, a WMV movie,
a movie inside an `.ifs`, a song without a movie and a song without chart data.

Format notes and credits: the music database layout follows
[drmext/iidx-datatools](https://github.com/drmext/iidx-datatools), the `.ifs`/kbin readers are ports of
[mon/ifstools](https://github.com/mon/ifstools) and [mon/kbinxml](https://github.com/mon/kbinxml),
chart slots follow [Glebsin/iidx2bms](https://github.com/Glebsin/iidx2bms), and the rendering approach
follows [Kuroshirowak/2dxrender](https://github.com/Kuroshirowak/2dxrender).
