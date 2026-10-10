#if DEBUG
// Developer aid, left out of release builds.
using System.Diagnostics;
using System.IO;
using Flow.Audio;
using Flow.Library;
using Flow.Services;
using Flow.Spotify;
using NAudio.Wave;

namespace Flow;

public partial class App
{
    /// <summary>
    /// Developer test: `Flow.exe --test-gapless &lt;file&gt;` checks song changes.
    /// <para>1. Offline, sample-exact: generated tones through the real mixing pipeline (WAV at the engine rate and
    /// at 44.1 kHz, MP3, AAC): gapless joins, gapless off, crossfade, crossfade on short songs.</para>
    /// <para>2. Real time, muted: local queues through PlaybackService (advancing, end of queue, repeat, next).</para>
    /// <para>3. Real time, muted, own Spotify device "Flow Test": Spotify to Spotify, local to Spotify, Spotify to local.</para>
    /// </summary>
    private async void RunGaplessProbe(string outFile)
    {
        var log = new List<string>();
        void L(string s) { log.Add($"[{DateTime.Now:HH:mm:ss.fff}] {s}"); File.WriteAllLines(outFile, log); }
        int fails = 0;
        void Check(bool ok, string what) { if (!ok) fails++; L((ok ? "  ok    " : "  FAIL  ") + what); }
        var dir = Path.Combine(Path.GetTempPath(), "flow-gapless-test");
        Directory.CreateDirectory(dir);
        LibrespotHost? librespot = null;
        PlaybackService? playback = null;
        try
        {
            // ---------------- 1. Offline ----------------
            var off = new AudioEngine { Volume = 1f };
            int rate = off.SampleRate;
            L($"engine rate {rate} Hz");

            string Tone(string name, int fileRate, double startSec, double lenSec, double hz, string kind = "wav")
            {
                var wav = Path.Combine(dir, name + ".wav");
                using (var w = new WaveFileWriter(wav, new WaveFormat(fileRate, 16, 2)))
                {
                    int n = (int)Math.Round(lenSec * fileRate);
                    var b = new byte[n * 4];
                    for (int i = 0; i < n; i++)
                    {
                        double t = startSec + i / (double)fileRate;
                        short v = (short)Math.Round(0.5 * Math.Sin(2 * Math.PI * hz * t) * 32767);
                        BitConverter.TryWriteBytes(b.AsSpan(i * 4), v);
                        BitConverter.TryWriteBytes(b.AsSpan(i * 4 + 2), v);
                    }
                    w.Write(b, 0, b.Length);
                }
                if (kind == "wav") return wav;
                var enc = Path.Combine(dir, name + "." + kind);
                using var r = new WaveFileReader(wav);
                if (kind == "mp3") MediaFoundationEncoder.EncodeToMp3(r, enc, 192000);
                else MediaFoundationEncoder.EncodeToAac(r, enc, 192000);
                return enc;
            }

            (float[] data, List<long> adv) Offline(string[] files, double cross, bool gapless)
            {
                off.CrossfadeSeconds = cross;
                off.Gapless = gapless;
                var tracks = files.Select(f => new Track { Path = f, Title = Path.GetFileName(f), ArtKey = "test" }).ToList();
                var adv = new List<long>();
                long pos = 0;
                bool ended = false;
                int k = 1;
                Action<TrackSource> onAdv = _ => { adv.Add(pos); if (k < tracks.Count) off.SetNext(TrackSource.Open(tracks[k++], rate, 1f)); };
                Action onEnd = () => ended = true;
                off.TrackAdvanced += onAdv;
                off.PlaybackEnded += onEnd;
                off.Load(TrackSource.Open(tracks[0], rate, 1f), play: false);
                if (tracks.Count > 1) off.SetNext(TrackSource.Open(tracks[k++], rate, 1f));
                var data = new List<float>();
                var buf = new float[480 * 2];
                var pipe = off.TestPipeline;
                for (int guard = 0; !ended && guard < 20000; guard++)
                {
                    int n = pipe.Read(buf, 0, buf.Length);
                    data.AddRange(buf.Take(n));
                    pos += n / 2;
                }
                off.TrackAdvanced -= onAdv;
                off.PlaybackEnded -= onEnd;
                off.Stop();
                return (data.ToArray(), adv);
            }

            // Length (to the last sound), longest silence inside, and the sharpest jump (a click) on the left channel.
            (long len, int quiet, long quietAt, double d2, long d2At) Analyze(float[] d)
            {
                long frames = d.Length / 2, last = 0;
                for (long i = 0; i < frames; i++) if (Math.Abs(d[i * 2]) > 0.003f) last = i;
                long len = last + 1;
                int run = 0, best = 0; long bestAt = 0;
                double d2 = 0; long d2At = 0;
                for (long i = 0; i < len; i++)
                {
                    if (Math.Abs(d[i * 2]) < 0.003f && Math.Abs(d[i * 2 + 1]) < 0.003f) { if (++run > best) { best = run; bestAt = i - run + 1; } }
                    else run = 0;
                    if (i >= rate / 200)
                    {
                        double x = Math.Abs(d[i * 2] - 2 * d[(i - 1) * 2] + d[(i - 2) * 2]);
                        if (x > d2) { d2 = x; d2At = i; }
                    }
                }
                return (len, best, bestAt, d2, d2At);
            }
            double Ms(long frames) => frames * 1000.0 / rate;
            double Rms(float[] d, long from, int frames)
            {
                double sum = 0;
                for (long i = from; i < from + frames; i++) sum += d[i * 2] * d[i * 2];
                return Math.Sqrt(sum / frames);
            }
            double cleanD2 = 0.5 * Math.Pow(2 * Math.PI * 440 / rate, 2);   // a smooth 440 Hz tone

            // One continuous 440 Hz tone cut into three 3-second songs: a perfect gapless join plays it back unbroken.
            void Join(string label, int fileRate, string kind, bool strict)
            {
                var files = new[] { Tone($"{kind}{fileRate}-a", fileRate, 0, 3, 440, kind), Tone($"{kind}{fileRate}-b", fileRate, 3, 3, 440, kind), Tone($"{kind}{fileRate}-c", fileRate, 6, 3, 440, kind) };
                var (data, adv) = Offline(files, 0, true);
                var a = Analyze(data);
                L($"{label}: length {Ms(a.len):0.0} ms (tone is 9000), longest silence {Ms(a.quiet):0.0} ms at {Ms(a.quietAt):0} ms, " +
                  $"sharpest jump {a.d2 / cleanD2:0.0}x a smooth tone at {Ms(a.d2At):0} ms, songs changed at {string.Join(", ", adv.Select(x => $"{Ms(x):0} ms"))}");
                if (strict)
                {
                    Check(Math.Abs(Ms(a.len) - 9000) < 3, $"{label}: nothing lost or added at the joins");
                    Check(a.quiet < 5, $"{label}: no silence at the joins");
                    Check(a.d2 < cleanD2 * 3, $"{label}: no click at the joins");
                }
                Check(adv.Count == 2, $"{label}: two song changes reported");
            }
            Join($"WAV {rate / 1000.0:0.#} kHz, gapless", rate, "wav", true);
            Join("WAV 44.1 kHz (resampled), gapless", 44100, "wav", true);
            Join("MP3 44.1 kHz, gapless", 44100, "mp3", false);
            Join("AAC/M4A 44.1 kHz, gapless", 44100, "m4a", false);

            // Gapless off: a short pause between songs.
            {
                var files = new[] { Tone("off-a", rate, 0, 3, 440), Tone("off-b", rate, 3, 3, 440) };
                var a = Analyze(Offline(files, 0, false).data);
                L($"Gapless off: length {Ms(a.len):0} ms, silence {Ms(a.quiet):0} ms at {Ms(a.quietAt):0} ms");
                Check(Math.Abs(Ms(a.quiet) - 400) < 15 && Math.Abs(Ms(a.quietAt) - 3000) < 15, "Gapless off: a 400 ms pause between songs");
            }

            // Crossfade 2 s: a 440 Hz song fading into a 660 Hz song, at even loudness.
            {
                var files = new[] { Tone("x-a", rate, 0, 6, 440), Tone("x-b", rate, 0, 6, 660), Tone("x-c", rate, 0, 6, 440) };
                var (data, adv) = Offline(files, 2, true);
                var a = Analyze(data);
                double steady = Rms(data, rate, rate);
                double lo = double.MaxValue, hi = 0;
                for (long f = (long)(3.6 * rate); f < 6.4 * rate; f += rate / 20) { double r = Rms(data, f, rate / 20); lo = Math.Min(lo, r); hi = Math.Max(hi, r); }
                double cleanD2b = 0.5 * Math.Pow(2 * Math.PI * 660 / rate, 2) + cleanD2;
                L($"Crossfade 2 s: length {Ms(a.len):0} ms (expect 14000), changes at {string.Join(", ", adv.Select(x => $"{Ms(x):0} ms"))} (expect 4000, 8000), " +
                  $"loudness in the fade {20 * Math.Log10(lo / steady):0.0} to {20 * Math.Log10(hi / steady):+0.0;-0.0} dB, longest silence {Ms(a.quiet):0} ms, sharpest jump {a.d2 / cleanD2b:0.00}x");
                Check(Math.Abs(Ms(a.len) - 14000) < 30, "Crossfade: songs overlap by 2 s");
                Check(adv.Count == 2 && Math.Abs(Ms(adv[0]) - 4000) < 30, "Crossfade: the next song starts 2 s before the end");
                Check(lo / steady > 0.8 && hi / steady < 1.25, "Crossfade: even loudness through the fade (no dip, no bump)");
                Check(a.quiet < 48 && a.d2 < cleanD2b * 1.5, "Crossfade: no silence or click");
            }

            // Crossfade on songs too short to fade (3 s songs, 2 s fade): played back to back instead.
            {
                var files = new[] { Tone("s-a", rate, 0, 3, 440), Tone("s-b", rate, 3, 3, 440) };
                var a = Analyze(Offline(files, 2, true).data);
                L($"Crossfade 2 s on 3 s songs: length {Ms(a.len):0} ms, silence {Ms(a.quiet):0} ms, jump {a.d2 / cleanD2:0.0}x");
                Check(Math.Abs(Ms(a.len) - 6000) < 3 && a.quiet < 5, "Short songs: played gapless instead of fading");
            }
            off.Dispose();

            // The built-in Spotify stream: two songs written back to back, the boundary marked as librespot's
            // events do, read the way the engine reads it.
            void Live(string label, double ahead, double fade, double expectLen, double expectSwitch, double expectFade)
            {
                const int lr = 44100;
                var li = new LiveInput(lr, 2, 13.5) { Attached = true };
                li.AheadSeconds = ahead;
                long switchedAt = -1, consumed = 0;
                byte[] Song(double hz, double sec)
                {
                    int n = (int)(sec * lr);
                    var b = new byte[n * 8];
                    for (int i = 0; i < n; i++)
                    {
                        float v = (float)(0.5 * Math.Sin(2 * Math.PI * hz * i / lr));
                        BitConverter.TryWriteBytes(b.AsSpan(i * 8), v);
                        BitConverter.TryWriteBytes(b.AsSpan(i * 8 + 4), v);
                    }
                    return b;
                }
                var a = Song(440, 6); var bb = Song(660, 6);
                bool done = false;
                var writer = new Thread(() =>
                {
                    for (int i = 0; i < a.Length; i += 3001) li.Write(a[i..Math.Min(a.Length, i + 3001)], Math.Min(3001, a.Length - i));
                    long mark = li.WrittenFrames;
                    li.SetBoundary(mark, fade, () => Interlocked.Exchange(ref switchedAt, Interlocked.Read(ref consumed)));
                    for (int i = 0; i < bb.Length; i += 2999) li.Write(bb[i..Math.Min(bb.Length, i + 2999)], Math.Min(2999, bb.Length - i));
                    done = true;
                });
                writer.Start();
                var data = new List<float>();
                var buf = new float[441 * 2];
                int quietTail = 0;
                while (quietTail < 20 && data.Count < lr * 2 * 30)
                {
                    if (!done && li.BufferedSeconds < ahead - 0.05) { Thread.Sleep(1); continue; }   // keep it as full as librespot keeps it
                    li.Read(buf, 0, buf.Length);
                    data.AddRange(buf);
                    Interlocked.Add(ref consumed, 441);
                    quietTail = done && li.BufferedSeconds == 0 ? quietTail + 1 : 0;
                    Thread.Sleep(2);
                }
                writer.Join();
                Thread.Sleep(50);
                var d = data.ToArray();
                long frames = d.Length / 2, last = 0;
                for (long i = 0; i < frames; i++) if (Math.Abs(d[i * 2]) > 0.003f) last = i;
                int run = 0, best = 0;
                for (long i = 0; i <= last; i++) { if (Math.Abs(d[i * 2]) < 0.003f) { if (++run > best) best = run; } else run = 0; }
                double steady = 0; for (long i = lr; i < 2 * lr; i++) steady += d[i * 2] * d[i * 2]; steady = Math.Sqrt(steady / lr);
                double lo = double.MaxValue, hi = 0;
                long fs = (long)((expectSwitch + 0.1) * lr), fe = (long)((expectSwitch + Math.Max(0.2, expectFade) - 0.1) * lr);
                for (long f = fs; f + lr / 20 < fe; f += lr / 20)
                {
                    double sum = 0; for (long i = f; i < f + lr / 20; i++) sum += d[i * 2] * d[i * 2];
                    double r = Math.Sqrt(sum / (lr / 20)); lo = Math.Min(lo, r); hi = Math.Max(hi, r);
                }
                double len = (last + 1) * 1000.0 / lr, sw = switchedAt * 1000.0 / lr;
                L($"{label}: length {len:0} ms (expect {expectLen * 1000:0}), new song audible at {sw:0} ms (expect {expectSwitch * 1000:0}), " +
                  $"longest silence {best * 1000.0 / lr:0.0} ms" + (expectFade > 0 ? $", loudness in the fade {20 * Math.Log10(lo / steady):0.0} to {20 * Math.Log10(hi / steady):+0.0;-0.0} dB" : ""));
                Check(Math.Abs(len - expectLen * 1000) < (ahead < fade ? 90 : 40), $"{label}: songs overlap by the right amount");
                Check(Math.Abs(sw - expectSwitch * 1000) < (ahead < fade ? 90 : 40), $"{label}: Flow is told at the right moment");
                Check(best < 48, $"{label}: no silence");
                if (expectFade > 0) Check(lo / steady > 0.8 && hi / steady < 1.25, $"{label}: even loudness through the fade");
            }
            Live("Spotify stream, gapless", 1, 0, 12, 6, 0);
            Live("Spotify stream, crossfade 3 s", 4, 3, 9, 3, 3);
            Live("Spotify stream, crossfade 3 s but only 1 s buffered", 1, 3, 11, 5, 1);

            // ---------------- 2. Real time, local songs ----------------
            var settings = new SettingsService();
            settings.Load();
            var s = settings.Current;
            s.SpotifyEngine = SpotifyEngine.BuiltIn;
            s.LibrespotDeviceName = "Flow Test";
            s.Volume = 0; s.Muted = true; s.Shuffle = false; s.CrossfadeSeconds = 0; s.Repeat = RepeatMode.Off;
            s.EqEnabled = false;
            var library = new LibraryService(settings, settings.DataDir);
            var engine = new AudioEngine();
            var spotify = new SpotifyService(settings, library);
            librespot = new LibrespotHost(settings, Dispatcher);
            playback = new PlaybackService(engine, library, settings, new SpotifyPlayback(spotify, Dispatcher, settings, librespot), Dispatcher, librespot);
            library.LoadFromDatabase();
            spotify.LoadCache();
            playback.Volume = 0;
            playback.IsMuted = true;
            var pb = playback;
            var watch = new GapWatch(engine.SampleRate);
            AudioEngine.TestTap = watch.Feed;

            var events = new List<string>();
            var sw = Stopwatch.StartNew();
            pb.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(PlaybackService.CurrentIndex)) events.Add($"{sw.ElapsedMilliseconds,6} ms  song {pb.CurrentIndex + 1}: {pb.CurrentTrack?.Title}");
                if (e.PropertyName == nameof(PlaybackService.IsPlaying)) events.Add($"{sw.ElapsedMilliseconds,6} ms  {(pb.IsPlaying ? "playing" : "stopped")}");
            };
            async Task<bool> WaitFor(Func<bool> cond, double seconds)
            {
                for (int i = 0; i < seconds * 20; i++) { if (cond()) return true; await Task.Delay(50); }
                return cond();
            }
            void Begin() { events.Clear(); watch.Reset(); sw.Restart(); }
            void Dump() { foreach (var e in events) L("    " + e); foreach (var g in watch.Take()) L("    audio: " + g); }

            Track Local(string name, double hz, double sec) =>
                new() { Path = Tone(name, rate, 0, sec, hz), Title = name, Artist = "Test", Album = "Test", ArtKey = "test", Duration = TimeSpan.FromSeconds(sec) };
            var q = new List<Track> { Local("Local 1", 330, 4), Local("Local 2", 440, 4), Local("Local 3", 550, 4) };

            async Task RunQueue(string label, double cross, bool gapless, int expectSilences, double expectMs)
            {
                engine.CrossfadeSeconds = cross;
                engine.Gapless = gapless;
                Begin();
                pb.PlayTracks(q, 0);
                await WaitFor(() => pb.IsPlaying, 5);
                bool done = await WaitFor(() => !pb.IsPlaying, 25);
                long took = sw.ElapsedMilliseconds;
                await Task.Delay(300);
                var gaps = watch.Take();
                L($"{label}: queue of three 4 s songs finished after {took} ms (expect about {expectMs:0})");
                foreach (var e in events) L("    " + e);
                foreach (var g in gaps) L("    audio: " + g);
                var songs = events.Where(e => e.Contains(" song ")).Select(e => e[(e.IndexOf("song") + 5)..(e.IndexOf(':', e.IndexOf("song")))]).ToList();
                Check(done && songs.SkipWhile(x => x == "1").Take(2).SequenceEqual(new[] { "2", "3" }), $"{label}: played songs 1, 2, 3 in order and stopped");
                Check(songs.LastOrDefault() == "1" && !pb.IsPlaying, $"{label}: parked on song 1 afterwards, not playing");
                Check(Math.Abs(took - expectMs) < 900, $"{label}: took the expected time");
                int silences = gaps.Count(g => g.Contains("silence") && !g.Contains("(end)"));
                Check(silences == expectSilences, $"{label}: {expectSilences} silence(s) between songs (heard {silences})");
            }
            await RunQueue("Local, gapless", 0, true, 0, 12000);
            await RunQueue("Local, gapless off", 0, false, 2, 12800);
            await RunQueue("Local, crossfade 1.5 s", 1.5, true, 0, 9000);
            engine.CrossfadeSeconds = 0; engine.Gapless = true;

            // Repeat One: the song starts over instead of moving on.
            {
                Begin();
                pb.Repeat = RepeatMode.One;
                pb.PlayTracks(q, 0);
                await WaitFor(() => pb.IsPlaying && pb.PositionSeconds > 1, 5);
                bool restarted = await WaitFor(() => pb.PositionSeconds < 1 && sw.ElapsedMilliseconds > 4000, 8);
                await Task.Delay(1500);
                L($"Repeat One: song {pb.CurrentIndex + 1} at {pb.PositionSeconds:0.0} s after {sw.ElapsedMilliseconds} ms, playing={pb.IsPlaying}");
                Check(restarted && pb.CurrentIndex == 0 && pb.IsPlaying, "Repeat One: song 1 started over");
                Check(!watch.Take().Any(g => g.Contains("silence")), "Repeat One: no silence at the restart");
                pb.Repeat = RepeatMode.Off;
            }
            // Repeat All: the last song wraps to the first.
            {
                pb.Repeat = RepeatMode.All;
                pb.PlayTracks(q, 2);
                await WaitFor(() => pb.IsPlaying && pb.PositionSeconds > 0.5, 5);
                Begin();
                bool wrapped = await WaitFor(() => pb.CurrentIndex == 0 && pb.PositionSeconds > 0.5, 8);
                L($"Repeat All: song {pb.CurrentIndex + 1}, playing={pb.IsPlaying}");
                Check(wrapped && pb.IsPlaying, "Repeat All: after the last song, song 1 plays");
                Check(!watch.Take().Any(g => g.Contains("silence")), "Repeat All: no silence at the wrap");
                pb.Repeat = RepeatMode.Off;
            }
            // Next, Previous and a seek near the end.
            {
                pb.PlayTracks(q, 0);
                await WaitFor(() => pb.IsPlaying && pb.PositionSeconds > 1.5, 5);
                Begin();
                pb.Next();
                bool n = false;
                for (int i = 0; i < 300 && !n; i++) { n = pb.CurrentIndex == 1 && pb.Engine.Position.TotalSeconds > 0.01; if (!n) await Task.Delay(5); }
                long nMs = sw.ElapsedMilliseconds;
                await Task.Delay(1500);
                pb.Previous();       // more than 3 s? no: 1.7 s in, so back to song 1
                bool p = await WaitFor(() => pb.CurrentIndex == 0, 3);
                await WaitFor(() => pb.PositionSeconds > 0.5, 3);
                pb.Seek(3.0);
                bool s2 = await WaitFor(() => pb.CurrentIndex == 1 && pb.PositionSeconds > 0.2, 3);
                L($"Next took {nMs} ms; Previous -> song {pb.CurrentIndex + 1}");
                Dump();
                Check(n && nMs < 150, "Next: the next local song plays within 0.15 s");
                Check(!watch.Take().Any(g => g.Contains("silence") && !g.Contains("(end)")), "Next: no silence");
                Check(p, "Previous: early in a song goes to the previous song");
                Check(s2, "Seek near the end: rolls into the next song");
                pb.Pause();
            }

            // ---------------- 3. Spotify (built-in engine) ----------------
            // Never take Spotify away from the person using it: skip this part if the account is playing elsewhere.
            var now = await spotify.GetAsync("/me/player");
            var where = now.Json?["device"]?["name"]?.GetValue<string>();
            bool busy = now.Json?["is_playing"]?.GetValue<bool>() == true && where != "Flow Test";
            if (busy) L($"Spotify part SKIPPED: the account is playing on {where}");
            else
            {
                await librespot.EnsureRunningAsync();
                await Task.Delay(3000);
                var album = library.Snapshot().Where(t => t.IsSpotify && t.SpotifyAlbumUri != null)
                    .GroupBy(t => t.SpotifyAlbumUri).Where(g => g.Count() >= 6).First()
                    .OrderBy(t => t.DiscNumber).ThenBy(t => t.TrackNumber).ToList();
                L($"Spotify album: {album[0].Album} ({album.Count} songs)");

                // Spotify to Spotify: let songs run out on their own, gapless twice, then with a 3 s crossfade twice.
                var logStart = DateTime.Now;
                var gaplessMs = new List<long>();
                {
                    pb.PlayTracks(album, 0);
                    await WaitFor(() => pb.IsPlaying && pb.PositionSeconds > 2 && watch.HeardRecently, 20);
                    for (int i = 0; i < 4; i++)
                    {
                        if (i == 2) { s.CrossfadeSeconds = 3; pb.CrossfadeChanged(); L("Crossfade set to 3 s"); }
                        var cur = pb.CurrentTrack!;
                        int idx = pb.CurrentIndex;
                        pb.Seek(cur.Duration.TotalSeconds - 8);
                        await Task.Delay(1500);
                        Begin();
                        bool moved = await WaitFor(() => pb.CurrentIndex == idx + 1, 15);
                        long movedMs = sw.ElapsedMilliseconds;
                        double posAtSwitch = pb.PositionSeconds;
                        await Task.Delay(2000);
                        double posLater = pb.PositionSeconds;
                        L($"Spotify -> Spotify ({cur.Title} -> {pb.CurrentTrack?.Title}): {(moved ? "moved on" : "STUCK")} {movedMs} ms after the 6.5 s mark; " +
                          $"clock {posAtSwitch:0.00} s at the switch, {posLater:0.00} s two seconds later; duration {pb.DurationSeconds:0} s (song is {pb.CurrentTrack?.Duration.TotalSeconds:0})");
                        // Seeking takes Spotify a moment, so the gapless changes set the yardstick: with a 3 s
                        // crossfade the next song must become audible (and show) 3 s earlier than that.
                        if (i < 2) gaplessMs.Add(movedMs);
                        double expect = gaplessMs.Average() - (s.CrossfadeSeconds > 0 ? 3000 : 0);
                        Check(moved && Math.Abs(movedMs - expect) < 400, $"Spotify -> Spotify: the new song shows when it is heard (expected about {expect:0} ms)");
                        Check(posAtSwitch < 0.6 && Math.Abs(posLater - posAtSwitch - 2) < 0.3, "Spotify -> Spotify: the new song's clock starts at 0 and runs true");
                        Check(Math.Abs(pb.DurationSeconds - (pb.CurrentTrack?.Duration.TotalSeconds ?? 0)) < 2, "Spotify -> Spotify: the new song's length is shown");
                        var gaps = watch.Take();
                        foreach (var e in events) L("    " + e);
                        foreach (var g in gaps) L("    audio: " + g);
                        Check(moved, "Spotify -> Spotify: the next song plays on its own");
                        Check(!gaps.Any(g => g.Contains("silence") && SilenceMs(g) > 400), "Spotify -> Spotify: no silence over 0.4 s");
                    }
                }
                var spLog = File.ReadAllLines(Path.Combine(settings.DataDir, "spotify.log"))
                    .Where(x => x.Length > 25 && DateTime.TryParse(x[1..24], out var when) && when >= logStart && x.Contains("Next song in the list")).ToList();
                foreach (var x in spLog) L("    " + x);
                Check(spLog.Count(x => x.Contains("crossfading 3 s")) == 2 && spLog.Count == 4, "Spotify -> Spotify: the switches came from librespot's events, the last two crossfaded");
                s.CrossfadeSeconds = 0;

                // Local to Spotify.
                {
                    var mixed = new List<Track> { Local("Local tone", 440, 4), album[2] };
                    Begin();
                    pb.PlayTracks(mixed, 0);
                    bool moved = await WaitFor(() => pb.CurrentIndex == 1 && pb.IsPlaying && pb.PositionSeconds > 1, 20);
                    await Task.Delay(800);
                    L($"Local -> Spotify: {(moved ? "moved on" : "STUCK")}");
                    var gaps = watch.Take();
                    foreach (var e in events) L("    " + e);
                    foreach (var g in gaps) L("    audio: " + g);
                    Check(moved, "Local -> Spotify: the Spotify song plays after the local one");
                    var gap = gaps.Where(g => g.Contains("silence")).Select(SilenceMs).DefaultIfEmpty(0).Max();
                    L($"    gap heard between them: {gap:0} ms");
                }
                // Spotify to local.
                {
                    var mixed = new List<Track> { album[3], Local("Local tone 2", 550, 4) };
                    pb.PlayTracks(mixed, 0);
                    await WaitFor(() => pb.IsPlaying && pb.PositionSeconds > 2 && watch.HeardRecently, 20);
                    pb.Seek(album[3].Duration.TotalSeconds - 5);
                    await Task.Delay(1500);
                    Begin();
                    bool moved = await WaitFor(() => pb.CurrentIndex == 1 && pb.IsPlaying && pb.PositionSeconds > 0.5, 15);
                    await Task.Delay(800);
                    L($"Spotify -> Local: {(moved ? "moved on" : "STUCK")} after {sw.ElapsedMilliseconds - 800} ms");
                    var gaps = watch.Take();
                    foreach (var e in events) L("    " + e);
                    foreach (var g in gaps) L("    audio: " + g);
                    Check(moved, "Spotify -> Local: the local song plays after the Spotify one");
                    var gap = gaps.Where(g => g.Contains("silence")).Select(SilenceMs).DefaultIfEmpty(0).Max();
                    L($"    gap heard between them: {gap:0} ms");
                    Check(gap < 150, "Spotify -> Local: the gap is under 0.15 s");
                }
            }
            L(fails == 0 ? "PASS" : $"CHECK: {fails} failed");
        }
        catch (Exception ex) { L("FAILED: " + ex); }
        finally
        {
            AudioEngine.TestTap = null;
            try { playback?.Pause(); } catch { }
            await Task.Delay(1000);
            try { librespot?.Stop(); } catch { }
            L("DONE");
            Environment.Exit(0);
        }
    }

    private static double SilenceMs(string gapLine)
    {
        int i = gapLine.IndexOf("silence ");
        if (i < 0) return 0;
        var num = new string(gapLine[(i + 8)..].TakeWhile(c => char.IsDigit(c) || c == '.').ToArray());
        return double.TryParse(num, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0;
    }

    /// <summary>Hears what the engine outputs (before volume) and notes every stretch of silence over 30 ms.</summary>
    private sealed class GapWatch
    {
        private readonly int _rate;
        private long _quiet, _lastRead;
        private readonly List<string> _gaps = new();
        private long _lastLoud;

        public GapWatch(int rate) => _rate = rate;

        public bool HeardRecently => Stopwatch.GetElapsedTime(Interlocked.Read(ref _lastLoud)).TotalMilliseconds < 300;

        public void Reset() { lock (_gaps) { _gaps.Clear(); _quiet = 0; _lastRead = 0; } }

        public List<string> Take()
        {
            lock (_gaps)
            {
                var l = _gaps.ToList();
                if (_quiet > _rate * 0.03) l.Add($"{DateTime.Now:HH:mm:ss.fff} silence {_quiet * 1000.0 / _rate:0} ms (end)");
                _gaps.Clear();
                return l;
            }
        }

        public void Feed(float[] b, int o, int c)
        {
            lock (_gaps)
            {
                long now = Stopwatch.GetTimestamp();
                // The output was stopped (between a local song and Spotify): that time was silent too.
                if (_lastRead != 0)
                {
                    double ms = Stopwatch.GetElapsedTime(_lastRead, now).TotalMilliseconds;
                    if (ms > 120) _quiet += (long)(ms / 1000 * _rate);
                }
                _lastRead = now;
                for (int i = o; i + 1 < o + c; i += 2)
                {
                    if (Math.Abs(b[i]) > 3e-4f || Math.Abs(b[i + 1]) > 3e-4f)
                    {
                        if (_quiet > _rate * 0.03) _gaps.Add($"{DateTime.Now:HH:mm:ss.fff} silence {_quiet * 1000.0 / _rate:0} ms");
                        _quiet = 0;
                        Interlocked.Exchange(ref _lastLoud, now);
                    }
                    else _quiet++;
                }
            }
        }
    }
}
#endif
