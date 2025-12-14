using Xunit;
using System;
using System.IO;
using Microsoft.Data.Sqlite;
using System.Collections.Generic;
using System.Linq;

namespace SoftMedia.Server.Tests;

public class DiagnosisTests
{
    [Fact(Skip = "Use for manual diagnosis only")]
    public void DiagnoseMissingArt()
    {
        var dbPath = @"c:\Users\Admin\Documents\coding2\SoftMedia\src\SoftMedia.Server\softmedia_debug_real.db";
        if (!File.Exists(dbPath)) throw new Exception($"DB not found at {dbPath}");

        var missing = new List<string>();

        using (var connection = new SqliteConnection($"Data Source={dbPath}"))
        {
            connection.Open();

            var command = connection.CreateCommand();
            // Get Audio tracks (MediaType 3) in Music libraries (LibraryType 2)
            command.CommandText = @"
                SELECT m.Title, m.MetadataJson, m.Id, m.Path, m.AlbumId
                FROM MediaItems m
                JOIN Libraries l ON m.LibraryId = l.Id
                WHERE l.Type = 2 AND m.Type = 3";

            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    var title = reader.GetString(0);
                    var json = reader.IsDBNull(1) ? null : reader.GetString(1);
                    var idObj = reader.GetValue(2);
                    var path = reader.GetString(3);
                    // AlbumId might be null

                    bool hasPoster = false;
                    
                    // 1. Check Embedded JSON
                    if (json != null && (json.Contains("\"poster\"") || json.Contains("hasEmbeddedArt")))
                    {
                        hasPoster = true;
                    }

                    // 2. Check MediaImages table
                    if (!hasPoster)
                    {
                        using (var cmd2 = connection.CreateCommand())
                        {
                            cmd2.CommandText = "SELECT COUNT(*) FROM MediaImages WHERE MediaItemId = $id AND ImageType = 'Poster'";
                            cmd2.Parameters.AddWithValue("$id", idObj);
                            var count = (long)cmd2.ExecuteScalar()!;
                            if (count > 0) hasPoster = true;
                        var failure = new 
                        { 
                            Path = path, 
                            Json = json 
                        };
                        missing.Add(System.Text.Json.JsonSerializer.Serialize(failure));
                    }
                }
            }
        }

            var msg = System.Text.Json.JsonSerializer.Serialize(missing, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            Console.WriteLine("########## FAILURE REPORT START ##########");
            Console.WriteLine(msg);
            Console.WriteLine("########## FAILURE REPORT END ##########");
            throw new Exception($"[DIAGNOSIS REPORT] Found {missing.Count} items.");
        }
    }
}
