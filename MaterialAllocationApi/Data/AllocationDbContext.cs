using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

public class AllocationDbContext : DbContext
{
    public AllocationDbContext(DbContextOptions<AllocationDbContext> options) : base(options) {}

    public DbSet<Sku> Skus => Set<Sku>();
    public DbSet<InventoryAdjustment> InventoryAdjustments => Set<InventoryAdjustment>();
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<OrderLine> OrderLines => Set<OrderLine>();
    public DbSet<Reservation> Reservations => Set<Reservation>();

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

        modelBuilder.Entity<Order>(b =>
        {
            b.ToTable("orders");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasColumnName("id").HasDefaultValueSql("gen_random_uuid()");
            b.Property(x => x.ReferenceCode).HasColumnName("reference_code").HasMaxLength(64).IsRequired();
            b.Property(x => x.Priority).HasColumnName("priority").HasMaxLength(32).IsRequired()
                .HasConversion(
                    v => v.ToDbString(),
                    v => OrderPriorityExtensions.FromDbString(v));
            b.Property(x => x.Status).HasColumnName("status").HasMaxLength(32).IsRequired()
                .HasConversion(
                    v => v.ToDbString(),
                    v => OrderStatusExtensions.FromDbString(v));
            b.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();

            // Uniqueness: reference_code is the caller-assigned natural key
            b.HasIndex(x => x.ReferenceCode).IsUnique().HasDatabaseName("idx_orders_reference_code");
            // Filter: list endpoint filters by status without scanning every order row
            b.HasIndex(x => x.Status).HasDatabaseName("idx_orders_status");
        });

        modelBuilder.Entity<OrderLine>(b =>
        {
            b.ToTable("order_lines", t =>
            {
                t.HasCheckConstraint("chk_order_lines_requested_positive",      "requested_qty > 0");
                t.HasCheckConstraint("chk_order_lines_allocated_non_negative",  "allocated_qty >= 0");
                t.HasCheckConstraint("chk_order_lines_allocated_lte_requested", "allocated_qty <= requested_qty");
            });
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasColumnName("id").HasDefaultValueSql("gen_random_uuid()");
            b.Property(x => x.OrderId).HasColumnName("order_id").IsRequired();
            b.Property(x => x.SkuId).HasColumnName("sku_id").IsRequired();
            b.Property(x => x.RequestedQty).HasColumnName("requested_qty").IsRequired();
            b.Property(x => x.AllocatedQty).HasColumnName("allocated_qty").IsRequired().HasDefaultValue(0);

            b.HasOne(x => x.Order)
            .WithMany(x => x.Lines)
            .HasForeignKey(x => x.OrderId)
            .OnDelete(DeleteBehavior.Cascade);

            b.HasOne(x => x.Sku)
            .WithMany()
            .HasForeignKey(x => x.SkuId)
            .OnDelete(DeleteBehavior.Restrict);

            // Uniqueness: one line per SKU per order — database enforcer for the aggregate rule
            b.HasIndex(x => new { x.OrderId, x.SkuId }).IsUnique().HasDatabaseName("idx_order_lines_order_sku");
            // FK lookup: allocation service loads all lines for an order; this covers the join
            b.HasIndex(x => x.OrderId).HasDatabaseName("idx_order_lines_order_id");
        });

        modelBuilder.Entity<Reservation>(b =>
        {
            b.ToTable("reservations", t =>
            {
                t.HasCheckConstraint("chk_reservations_quantity_positive", "quantity > 0");
            });
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasColumnName("id").HasDefaultValueSql("gen_random_uuid()");
            b.Property(x => x.OrderLineId).HasColumnName("order_line_id").IsRequired();
            b.Property(x => x.Quantity).HasColumnName("quantity").IsRequired();
            b.Property(x => x.ExpiresAt).HasColumnName("expires_at").IsRequired();
            b.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();

            b.HasOne(x => x.OrderLine)
            .WithMany()
            .HasForeignKey(x => x.OrderLineId)
            .OnDelete(DeleteBehavior.Cascade);
            
            // Expiry job scans by expires_at; this index is critical for the DELETE WHERE expires_at <= NOW() to stay fast.
            b.HasIndex(x => x.ExpiresAt).HasDatabaseName("idx_reservations_expiry");
            // Reserve and release operations look up by order_line_id.
            b.HasIndex(x => x.OrderLineId).HasDatabaseName("idx_reservations_order_line_id");
        });
    }
}