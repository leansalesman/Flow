using System.IO;
using Microsoft.Data.Sqlite;

namespace Flow.Library;

/// <summary>SQLite persistence for tracks, play stats and playlists. All calls are serialized.</summary>
public sealed class LibraryDb : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly object _lock = new();

    public LibraryDb(string dataDir)
    {
        Directory.CreateDirectory(dataDir);
        _conn = new SqliteConnection($"Data Source={Path.Combine(dataDir, "library.db")}");
        _conn.Open();
        Exec("PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;");
        Exec("""
            CREATE TABLE IF NOT EXISTS Tracks(
              Id INTEGER PRIMARY KEY AUTOINCREMENT,
              Path TEXT NOT NULL UNIQUE COLLATE NOCASE,
              Title TEXT, Artist TEXT, AlbumArtist TEXT, Album TEXT, Genre TEXT,
              Year INTEGER, TrackNo INTEGER, DiscNo INTEGER, DurationMs INTEGER,
              Bitrate INTEGER, SampleRate INTEGER, Format TEXT, FileSize INTEGER, Mtime INTEGER,
              DateAdded INTEGER, PlayCount INTEGER DEFAULT 0, LastPlayed INTEGER, Favorite INTEGER DEFAULT 0,
              RgTrackGain REAL, RgTrackPeak REAL, RgAlbumGain REAL, RgAlbumPeak REAL, ArtKey TEXT);
            CREATE TABLE IF NOT EXISTS Playlists(
              Id INTEGER PRIMARY KEY AUTOINCREMENT, Name TEXT NOT NULL, Created INTEGER);
            CREATE TABLE IF NOT EXISTS PlaylistTracks(
              PlaylistId INTEGER NOT NULL, Position INTEGER NOT NULL, Path TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS IX_PlaylistTracks ON PlaylistTracks(PlaylistId, Position);
            CREATE TABLE IF NOT EXISTS Stats(
              Path TEXT PRIMARY KEY COLLATE NOCASE, PlayCount INTEGER, LastPlayed INTEGER, Favorite INTEGER);
            """);
    }

    private void Exec(string sql)
    {
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }
    }

    public List<Track> LoadTracks()
    {
        var list = new List<Track>();
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT Id,Path,Title,Artist,AlbumArtist,Album,Genre,Year,TrackNo,DiscNo,DurationMs,Bitrate,SampleRate," +
                              "Format,FileSize,Mtime,DateAdded,PlayCount,LastPlayed,Favorite,RgTrackGain,RgTrackPeak,RgAlbumGain,RgAlbumPeak,ArtKey FROM Tracks";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                list.Add(new Track
                {
                    Id = r.GetInt64(0),
                    Path = r.GetString(1),
                    Title = Str(r, 2), Artist = Pool(Str(r, 3)), AlbumArtist = Pool(Str(r, 4)), Album = Pool(Str(r, 5)), Genre = Pool(Str(r, 6)),
                    Year = Int(r, 7), TrackNumber = Int(r, 8), DiscNumber = Int(r, 9),
                    Duration = TimeSpan.FromMilliseconds(Long(r, 10)),
                    Bitrate = Int(r, 11), SampleRate = Int(r, 12), Format = Pool(Str(r, 13)),
                    FileSize = Long(r, 14), Mtime = Long(r, 15),
                    DateAdded = new DateTime(Long(r, 16) is var d && d > 0 ? d : DateTime.Now.Ticks),
                    PlayCount = Int(r, 17),
                    LastPlayed = r.IsDBNull(18) ? null : new DateTime(r.GetInt64(18)),
                    IsFavorite = Int(r, 19) != 0,
                    RgTrackGain = Dbl(r, 20), RgTrackPeak = Dbl(r, 21), RgAlbumGain = Dbl(r, 22), RgAlbumPeak = Dbl(r, 23),
                    ArtKey = Pool(Str(r, 24)),
                });
            }
        }
        return list;
    }

    private static string Pool(string s) => s.Length == 0 ? "" : string.Intern(s);
    private static string Str(SqliteDataReader r, int i) => r.IsDBNull(i) ? "" : r.GetString(i);
    private static int Int(SqliteDataReader r, int i) => r.IsDBNull(i) ? 0 : r.GetInt32(i);
    private static long Long(SqliteDataReader r, int i) => r.IsDBNull(i) ? 0 : r.GetInt64(i);
    private static double? Dbl(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetDouble(i);
    private static object N(double? v) => v.HasValue ? v.Value : DBNull.Value;

    public void Upsert(IEnumerable<Track> tracks)
    {
        lock (_lock)
        {
            using var tx = _conn.BeginTransaction();
            using var cmd = _conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO Tracks(Path,Title,Artist,AlbumArtist,Album,Genre,Year,TrackNo,DiscNo,DurationMs,Bitrate,SampleRate,
                  Format,FileSize,Mtime,DateAdded,PlayCount,LastPlayed,Favorite,RgTrackGain,RgTrackPeak,RgAlbumGain,RgAlbumPeak,ArtKey)
                VALUES($p,$t,$a,$aa,$al,$g,$y,$tn,$dn,$d,$br,$sr,$f,$fs,$m,$da,$pc,$lp,$fav,$rtg,$rtp,$rag,$rap,$ak)
                ON CONFLICT(Path) DO UPDATE SET Title=$t,Artist=$a,AlbumArtist=$aa,Album=$al,Genre=$g,Year=$y,TrackNo=$tn,DiscNo=$dn,
                  DurationMs=$d,Bitrate=$br,SampleRate=$sr,Format=$f,FileSize=$fs,Mtime=$m,
                  RgTrackGain=$rtg,RgTrackPeak=$rtp,RgAlbumGain=$rag,RgAlbumPeak=$rap,ArtKey=$ak
                RETURNING Id;
                """;
            var ps = new[] { "$p","$t","$a","$aa","$al","$g","$y","$tn","$dn","$d","$br","$sr","$f","$fs","$m","$da","$pc","$lp","$fav","$rtg","$rtp","$rag","$rap","$ak" };
            foreach (var p in ps) cmd.Parameters.Add(new SqliteParameter(p, null));
            foreach (var t in tracks)
            {
                var v = new object[]
                {
                    t.Path, t.Title, t.Artist, t.AlbumArtist, t.Album, t.Genre, t.Year, t.TrackNumber, t.DiscNumber,
                    (long)t.Duration.TotalMilliseconds, t.Bitrate, t.SampleRate, t.Format, t.FileSize, t.Mtime,
                    t.DateAdded.Ticks, t.PlayCount, t.LastPlayed.HasValue ? t.LastPlayed.Value.Ticks : DBNull.Value,
                    t.IsFavorite ? 1 : 0, N(t.RgTrackGain), N(t.RgTrackPeak), N(t.RgAlbumGain), N(t.RgAlbumPeak), t.ArtKey
                };
                for (int i = 0; i < v.Length; i++) cmd.Parameters[i].Value = v[i] ?? DBNull.Value;
                var id = cmd.ExecuteScalar();
                if (id is long l) t.Id = l;
            }
            tx.Commit();
        }
    }

    public void Delete(IEnumerable<string> paths)
    {
        lock (_lock)
        {
            using var tx = _conn.BeginTransaction();
            using var cmd = _conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "DELETE FROM Tracks WHERE Path=$p";
            var p = cmd.Parameters.Add(new SqliteParameter("$p", ""));
            foreach (var path in paths) { p.Value = path; cmd.ExecuteNonQuery(); }
            tx.Commit();
        }
    }

    /// <summary>Saves play count / last played / favorite. Kept in a separate table so stats survive rescans.</summary>
    public void SaveStats(Track t)
    {
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO Stats(Path,PlayCount,LastPlayed,Favorite) VALUES($p,$c,$l,$f)
                ON CONFLICT(Path) DO UPDATE SET PlayCount=$c,LastPlayed=$l,Favorite=$f;
                UPDATE Tracks SET PlayCount=$c,LastPlayed=$l,Favorite=$f WHERE Path=$p;
                """;
            cmd.Parameters.AddWithValue("$p", t.Path);
            cmd.Parameters.AddWithValue("$c", t.PlayCount);
            cmd.Parameters.AddWithValue("$l", t.LastPlayed.HasValue ? t.LastPlayed.Value.Ticks : DBNull.Value);
            cmd.Parameters.AddWithValue("$f", t.IsFavorite ? 1 : 0);
            cmd.ExecuteNonQuery();
        }
    }

    public Dictionary<string, (int PlayCount, DateTime? LastPlayed, bool Favorite)> LoadStats()
    {
        var d = new Dictionary<string, (int, DateTime?, bool)>(StringComparer.OrdinalIgnoreCase);
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT Path,PlayCount,LastPlayed,Favorite FROM Stats";
            using var r = cmd.ExecuteReader();
            while (r.Read())
                d[r.GetString(0)] = (Int(r, 1), r.IsDBNull(2) ? null : new DateTime(r.GetInt64(2)), Int(r, 3) != 0);
        }
        return d;
    }

    // ---- Playlists ----

    public List<PlaylistInfo> GetPlaylists()
    {
        var list = new List<PlaylistInfo>();
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT Id,Name FROM Playlists ORDER BY Name COLLATE NOCASE";
            using var r = cmd.ExecuteReader();
            while (r.Read()) list.Add(new PlaylistInfo { Id = r.GetInt64(0), Name = r.GetString(1) });
        }
        return list;
    }

    public long CreatePlaylist(string name)
    {
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "INSERT INTO Playlists(Name,Created) VALUES($n,$c) RETURNING Id";
            cmd.Parameters.AddWithValue("$n", name);
            cmd.Parameters.AddWithValue("$c", DateTime.Now.Ticks);
            return (long)cmd.ExecuteScalar()!;
        }
    }

    public void RenamePlaylist(long id, string name)
    {
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "UPDATE Playlists SET Name=$n WHERE Id=$i";
            cmd.Parameters.AddWithValue("$n", name);
            cmd.Parameters.AddWithValue("$i", id);
            cmd.ExecuteNonQuery();
        }
    }

    public void DeletePlaylist(long id)
    {
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "DELETE FROM PlaylistTracks WHERE PlaylistId=$i; DELETE FROM Playlists WHERE Id=$i;";
            cmd.Parameters.AddWithValue("$i", id);
            cmd.ExecuteNonQuery();
        }
    }

    public List<string> GetPlaylistPaths(long id)
    {
        var list = new List<string>();
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT Path FROM PlaylistTracks WHERE PlaylistId=$i ORDER BY Position";
            cmd.Parameters.AddWithValue("$i", id);
            using var r = cmd.ExecuteReader();
            while (r.Read()) list.Add(r.GetString(0));
        }
        return list;
    }

    public void SetPlaylistPaths(long id, IEnumerable<string> paths)
    {
        lock (_lock)
        {
            using var tx = _conn.BeginTransaction();
            using (var del = _conn.CreateCommand())
            {
                del.Transaction = tx;
                del.CommandText = "DELETE FROM PlaylistTracks WHERE PlaylistId=$i";
                del.Parameters.AddWithValue("$i", id);
                del.ExecuteNonQuery();
            }
            using var cmd = _conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO PlaylistTracks(PlaylistId,Position,Path) VALUES($i,$pos,$p)";
            cmd.Parameters.AddWithValue("$i", id);
            var pos = cmd.Parameters.Add(new SqliteParameter("$pos", 0));
            var p = cmd.Parameters.Add(new SqliteParameter("$p", ""));
            int n = 0;
            foreach (var path in paths) { pos.Value = n++; p.Value = path; cmd.ExecuteNonQuery(); }
            tx.Commit();
        }
    }

    public void Dispose()
    {
        lock (_lock) _conn.Dispose();
    }
}
