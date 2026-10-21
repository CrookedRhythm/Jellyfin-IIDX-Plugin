#!/usr/bin/env python3
"""Builds a small fake IIDX `contents` folder for testing the plugin without real game data.

Songs (all tones and test patterns generated with ffmpeg, nothing from the game):
  01000  sound/01000.ifs with .1 + .s3p (WMA keysounds), H.264 movie (loose mp4)
  01001  sound/01001.ifs with .1 + .2dx (MS-ADPCM keysounds), WMV movie (loose)
  01002  sound/01002/ loose .1 + .2dx (PCM keysounds), H.264 movie inside movie/movie_pack.ifs
  01003  .ifs chart data but no movie (renders over a black frame)
  01004  in the music db but without chart data (must be skipped)

Requires ffmpeg on PATH and `ifstools` (pip install ifstools) for packing .ifs archives.

usage: make_test_gamedata.py <output contents dir> [--version 33] [--seconds 20]
"""
import argparse
import math
import os
import shutil
import struct
import subprocess
import tempfile

SR = 44100


def ffmpeg(*args):
    subprocess.run(["ffmpeg", "-hide_banner", "-loglevel", "error", "-y", *args], check=True)


def tone_wav(path, freq, seconds, codec):
    ffmpeg("-f", "lavfi", "-i", f"sine=frequency={freq}:sample_rate={SR}:duration={seconds}",
           "-af", "afade=t=out:st=%f:d=%f" % (seconds * 0.6, seconds * 0.4), "-ac", "2", "-c:a", codec, path)


def wma(path, freq, seconds):
    ffmpeg("-f", "lavfi", "-i", f"sine=frequency={freq}:sample_rate={SR}:duration={seconds}",
           "-ac", "2", "-c:a", "wmav2", "-b:a", "128k", "-f", "asf", path)


def make_2dx(name, wavs):
    header = struct.pack("<16sII", name.encode()[:16].ljust(16, b"\0"), 72 + 4 * len(wavs), len(wavs)) + b"\0" * 48
    offsets, body = [], b""
    base = 72 + 4 * len(wavs)
    for w in wavs:
        offsets.append(base + len(body))
        body += struct.pack("<4sIIhhhhi", b"2DX9", 24, len(w), 0x3231, -1, 64, 1, 0) + w
    return header + b"".join(struct.pack("<I", o) for o in offsets) + body


def make_s3p(wmas):
    out = b"S3P0" + struct.pack("<I", len(wmas))
    table, body = b"", b""
    base = 8 + 8 * len(wmas)
    for w in wmas:
        entry = b"S3V0" + struct.pack("<II", 32, len(w)) + b"\0" * 20 + w
        table += struct.pack("<II", base + len(body), len(entry))
        body += entry
    return out + table + body


def make_chart(bpm, seconds, keysound_count):
    """A chart that loads lane sounds, plays notes on every beat, and a BGM keysound each bar."""
    beat = 60000.0 / bpm
    ev = [(0, 4, 1, bpm), (0, 5, 4, 4)]
    for lane in range(8):
        ev.append((0, 2, lane, 2 + lane % (keysound_count - 1)))
    t, i = 0.0, 0
    while t < seconds * 1000 - 1500:
        if i % 4 == 0:
            ev.append((int(t), 7, 0, 1))  # BGM keysound 1
            ev.append((int(t), 12, 0, 0))  # bar line
        lane = i % 8
        ev.append((int(t), 0, lane, 0))
        if i == 16:
            ev.append((int(t), 2, 0, 2 + (i % (keysound_count - 1))))  # swap lane 0's sound
        t += beat
        i += 1
    ev.append((int(seconds * 1000), 6, 0, 0))
    ev.append((int(seconds * 1000), 6, 1, 0))
    body = b"".join(struct.pack("<iBBH", *e) for e in sorted(ev, key=lambda e: e[0])) + struct.pack("<iBBH", 0x7FFFFFFF, 0, 0, 0)
    header = bytearray(96)
    # slots: 0 SPH, 1 SPN, 2 SPA. All three use the same events here.
    for slot in (0, 1, 2):
        struct.pack_into("<II", header, slot * 8, 96 + slot * len(body), len(body))
    return bytes(header) + body * 3


def music_db(version, songs):
    modern = version >= 32
    max_entries = version * 1000 + 1000
    out = b"IIDX"
    out += struct.pack("<IHHI", version, len(songs), 0, max_entries) if modern else struct.pack("<IHHI", version, len(songs), max_entries, 0)
    ids = {s["id"]: i for i, s in enumerate(songs)}
    fmt = "<i" if modern else "<h"
    for i in range(max_entries):
        out += struct.pack(fmt, ids[i] if i in ids else (0 if i >= version * 1000 else -1))

    def s(text, n, enc):
        b = text.encode(enc)[:n]
        return b + b"\0" * (n - len(b))

    for song in sorted(songs, key=lambda x: x["id"]):
        e = b""
        if modern:
            e += s(song["title"], 0x100, "utf-16-le") + s(song["ascii"], 0x40, "cp932") + s(song["genre"], 0x80, "utf-16-le")
            e += s(song["artist"], 0x100, "utf-16-le") + s(song.get("subtitle", ""), 0x100, "utf-16-le")
        else:
            e += s(song["title"], 0x40, "cp932") + s(song["ascii"], 0x40, "cp932") + s(song["genre"], 0x40, "cp932") + s(song["artist"], 0x40, "cp932")
        e += struct.pack("<IIIII", 0, 0, 0, 0, 0)
        if modern:
            e += struct.pack("<I", 0)
        e += struct.pack("<IH", 0, song["version"])
        e += struct.pack("<HHHHHHH", 0, 0, 0, 0, 0, 1, 0) if modern else struct.pack("<HHH", 0, 0, 1)
        lv = song["levels"]  # SPB SPN SPH SPA SPL DPB DPN DPH DPA DPL
        if version >= 27:
            e += bytes(lv)
        else:
            e += bytes([lv[1], lv[2], lv[3], lv[6], lv[7], lv[8], lv[0], lv[5]])
        e += b"\0" * (0x286 if version >= 27 else 0xA0)
        e += struct.pack("<II", song["id"], 100)
        e += bytes([48] * (10 if version >= 27 else 8))
        e += struct.pack("<h", song.get("delay", 0))
        if version <= 26:
            e += b"\0\0"
        e += s(song["bga"], 0x20, "cp932")
        e += struct.pack("<I", 0) + b"\0" * (0x20 * (10 if version >= 22 else 9))
        if version >= 26:
            e += b"\0" * 4
        out += e
    return out


def pack_ifs(folder, out_path):
    """Packs `folder` (which must end in _ifs) into an .ifs next to it, then moves it to out_path."""
    subprocess.run(["ifstools", "-y", "-o", os.path.dirname(folder), folder], check=True, stdout=subprocess.DEVNULL)
    shutil.move(folder[:-4] + ".ifs", out_path)
    shutil.rmtree(folder)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("out")
    ap.add_argument("--version", type=int, default=33)
    ap.add_argument("--seconds", type=float, default=20)
    a = ap.parse_args()

    data = os.path.join(a.out, "data")
    for d in ("info/0", "sound", "movie"):
        os.makedirs(os.path.join(data, d), exist_ok=True)
    tmp = tempfile.mkdtemp()
    freqs = [110, 262, 294, 330, 349, 392, 440, 494, 523]

    def tones(codec, ext):
        files = []
        for i, f in enumerate(freqs):
            p = os.path.join(tmp, f"t{i}.{ext}")
            # keysound 1 is a long BGM pad, the rest are short notes
            if codec == "wma":
                wma(p, f, 1.5 if i == 0 else 0.3)
            else:
                tone_wav(p, f, 1.5 if i == 0 else 0.3, codec)
            files.append(open(p, "rb").read())
        return files

    chart = make_chart(150, a.seconds, len(freqs))
    pre = make_2dx("pre", [tones("pcm_s16le", "wav")[1]])

    def song_ifs(sid, keyname, keys):
        folder = os.path.join(tmp, f"{sid}_ifs")
        os.makedirs(os.path.join(folder, sid))
        open(os.path.join(folder, sid, f"{sid}.1"), "wb").write(chart)
        open(os.path.join(folder, sid, f"{sid}{keyname}"), "wb").write(keys)
        open(os.path.join(folder, sid, f"{sid}_pre.2dx"), "wb").write(pre)
        pack_ifs(folder, os.path.join(data, "sound", f"{sid}.ifs"))

    song_ifs("01000", ".s3p", make_s3p(tones("wma", "wma")))
    song_ifs("01001", ".2dx", make_2dx("01001", tones("adpcm_ms", "wav")))
    loose = os.path.join(data, "sound", "01002")
    os.makedirs(loose, exist_ok=True)
    open(os.path.join(loose, "01002.1"), "wb").write(chart)
    open(os.path.join(loose, "01002.2dx"), "wb").write(make_2dx("01002", tones("pcm_s16le", "wav")))
    song_ifs("01003", ".2dx", make_2dx("01003", tones("pcm_s16le", "wav")))

    mv = a.seconds + 5
    ffmpeg("-f", "lavfi", "-i", f"testsrc2=size=1280x720:rate=30:duration={mv}", "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", os.path.join(data, "movie", "01000.mp4"))
    ffmpeg("-f", "lavfi", "-i", f"smptebars=size=640x480:rate=30:duration={mv / 2}", "-c:v", "wmv2", "-f", "asf", os.path.join(data, "movie", "01001.wmv"))
    pack = os.path.join(tmp, "movie_pack_ifs")
    os.makedirs(pack)
    ffmpeg("-f", "lavfi", "-i", f"mandelbrot=size=640x360:rate=30", "-t", str(mv), "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", os.path.join(pack, "song_02.mp4"))
    pack_ifs(pack, os.path.join(data, "movie", "movie_pack.ifs"))

    lv = lambda a_: [0, 3, 6, a_, 0, 0, 3, 6, a_, 0]
    songs = [
        dict(id=1000, title="テスト曲", ascii="Test Song S3P", genre="TEST TECHNO", artist="Tester", version=33, levels=lv(10), bga="01000", delay=-120),
        dict(id=1001, title="ADPCM Song", ascii="ADPCM Song", genre="TEST TRANCE", artist="DJ Adpcm", version=17, levels=[0, 4, 7, 11, 12, 0, 4, 7, 11, 0], bga="01001", delay=0),
        dict(id=1002, title="Loose PCM", ascii="Loose PCM", genre="TEST HOUSE", artist="Loose Files", version=25, levels=lv(9), bga="song_02", delay=60),
        dict(id=1003, title="No Movie", ascii="No Movie", genre="TEST AMBIENT", artist="Nobody", version=33, levels=lv(5), bga="missing"),
        dict(id=1004, title="No Chart", ascii="No Chart", genre="TEST", artist="Nobody", version=33, levels=lv(1), bga=""),
        dict(id=1005, title="", ascii="", genre="event_data", artist="event_data", version=33, levels=lv(0), bga=""),
    ]
    open(os.path.join(data, "info", "0", "music_data.bin"), "wb").write(music_db(a.version, songs))
    shutil.rmtree(tmp)
    print("wrote", a.out)


if __name__ == "__main__":
    main()
