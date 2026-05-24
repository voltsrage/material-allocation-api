using Microsoft.EntityFrameworkCore;

public class AllocationDbContext : DbContext
{
    public AllocationDbContext(DbContextOptions<AllocationDbContext> options) : base(options) {}

    public DbSet<Sku> Skus => Set<Sku>();
    public DbSet<InventoryAdjustment> InventoryAdjustments => Set<InventoryAdjustment>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Sku>(b =>
        {
            // Database-level safety net: rejects any row that bypasses the domain method
            b.ToTable("skus", t => t.HasCheckConstraint("chk_skus_on_hand_non_negative", "on_hand >= 0"));
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasColumnName("id").HasDefaultValueSql("gen_random_uuid()");
            b.Property(x => x.SkuCode).HasColumnName("sku_code").HasMaxLength(64).IsRequired();
            b.Property(x => x.Description).HasColumnName("description").HasMaxLength(500).IsRequired();
            b.Property(x => x.OnHand).HasColumnName("on_hand").IsRequired();
            // IsConcurrencyToken: EF appends WHERE version = @expected to every UPDATE on this row
            // A concurrent allocate that modified the row between our read and our write causes
            // the UPDATE to match zero rows, triggering DbUpdateConcurrencyException in Phase 4
            b.Property(x => x.Version).HasColumnName("version").IsConcurrencyToken().HasDefaultValue(0);
            b.Property(x => x.UpdatedAt).HasColumnName("updated_at").IsRequired();
            // Uniqueness: sku_code is the natural key referenced by order lines at creation time
            b.HasIndex(x => x.SkuCode).IsUnique().HasDatabaseName("idx_skus_sku_code");
            
        });

        modelBuilder.Entity<InventoryAdjustment>(b =>
        {
            b.ToTable("inventory_adjustments");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasColumnName("id").HasDefaultValueSql("gen_random_uuid()");
            b.Property(x => x.SkuId).HasColumnName("sku_id").IsRequired();
            b.Property(x => x.Delta).HasColumnName("delta").IsRequired();
            b.Property(x => x.Reason).HasColumnName("reason").HasMaxLength(500).IsRequired();
            b.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();

            b.HasOne(x => x.Sku)
             .WithMany(x => x.Adjustments)
             .HasForeignKey(x => x.SkuId)
             .OnDelete(DeleteBehavior.Restrict);

            // Lookup: the adjust audit endpoint lists all adjustments for a single SKU
            b.HasIndex(x => x.SkuId).HasDatabaseName("idx_adjustments_sku_id");
        });
    }
}