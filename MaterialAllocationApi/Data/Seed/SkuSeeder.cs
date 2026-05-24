using Microsoft.EntityFrameworkCore;

public static class SkuSeeder
{
    public static async Task SeedAsync(AllocationDbContext db)
    {
        if (await db.Skus.AnyAsync())
            return;

        var skus = new[]
        {
            new Sku("MEM-DDR5-16G",  "DDR5 16 GB DIMM — 4800 MHz",   100),
            new Sku("MEM-DDR5-32G",  "DDR5 32 GB DIMM — 4800 MHz",    50),
            new Sku("MEM-DDR4-8G",   "DDR4 8 GB DIMM — 3200 MHz",    200),
            new Sku("NAND-512G-MLC", "512 GB MLC NAND Flash Module",    1),
            new Sku("NAND-1T-TLC",   "1 TB TLC NAND Flash Module",     10),
        };

        db.Skus.AddRange(skus);
        await db.SaveChangesAsync();

        Console.WriteLine($"Seeded {skus.Length} SKUs.");
    }
}