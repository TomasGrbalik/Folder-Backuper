using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace FolderBackuper.Infrastructure.Database;

public sealed class FolderBackuperDesignTimeDbContextFactory
    : IDesignTimeDbContextFactory<FolderBackuperDbContext>
{
    public FolderBackuperDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<FolderBackuperDbContext>()
            .UseSqlite("Data Source=folder-backuper.design.db")
            .Options;
        return new FolderBackuperDbContext(options);
    }
}
