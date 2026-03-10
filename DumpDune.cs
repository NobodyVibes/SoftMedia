using System;
using Microsoft.Data.Sqlite;

class Program
{
    static void Main()
    {
        var dbPath = @"c:\Users\Admin\Documents\coding2\SoftMedia\src\SoftMedia.Server\softmedia.db";
        using var connection = new SqliteConnection($"Data Source={dbPath}");
        connection.Open();
        
        var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, Title, MetadataJson, CoverArtPath FROM MediaItems WHERE Title = 'Dune'";
        
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            Console.WriteLine($"Id: {reader.GetGuid(0)}");
            Console.WriteLine($"Title: {reader.GetString(1)}");
            Console.WriteLine($"MetadataJson: {(reader.IsDBNull(2) ? "null" : reader.GetString(2))}");
            Console.WriteLine($"CoverArtPath: {(reader.IsDBNull(3) ? "null" : reader.GetString(3))}");
            Console.WriteLine("------------------");
        }
    }
}
