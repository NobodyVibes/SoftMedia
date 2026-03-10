using System;
using System.Linq;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;
using SoftMedia.Server.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace SoftMedia.Server.Tests
{
    public class DumpDuneTest
    {
        private readonly ITestOutputHelper _output;

        public DumpDuneTest(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public async Task DumpDuneMetadata()
        {
            var dbPath = @"c:\Users\Admin\Documents\coding2\SoftMedia\src\SoftMedia.Server\softmedia.db";
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite($"Data Source={dbPath}")
                .Options;

            using var context = new AppDbContext(options);
            var targetId = Guid.Parse("c0971974-137e-436d-8b57-5d6c059f50b7");
            var items = await context.MediaItems.Where(m => m.Id == targetId).ToListAsync();
            
            var sb = new System.Text.StringBuilder();
            foreach(var item in items)
            {
                sb.AppendLine($"Id: {item.Id}");
                sb.AppendLine($"Title: {item.Title}");
                sb.AppendLine($"MetadataJson: {item.MetadataJson}");
                sb.AppendLine($"CoverArtPath: {item.CoverArtPath}");
                sb.AppendLine("------------------");
            }
            System.IO.File.WriteAllText(@"c:\Users\Admin\Documents\coding2\SoftMedia\dunedump.txt", sb.ToString());
        }
    }
}
