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
    public DbSet<AllocationEvent> AllocationEvents => Set<AllocationEvent>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();
    public DbSet<IdempotencyRecord> IdempotencyRecords => Set<IdempotencyRecord>();
    public DbSet<AllocationRun> AllocationRuns => Set<AllocationRun>();
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<CustomerContract> CustomerContracts => Set<CustomerContract>();
    public DbSet<Lot> Lots => Set<Lot>();
    public DbSet<LotEvent> LotEvents => Set<LotEvent>();

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
            b.Property(x => x.CustomerId).HasColumnName("customer_id");
            b.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();

            b.HasOne(x => x.Customer)
                .WithMany()
                .HasForeignKey(x => x.CustomerId)
                .OnDelete(DeleteBehavior.Restrict)
                .IsRequired(false);

            // Uniqueness: reference_code is the caller-assigned natural key
            b.HasIndex(x => x.ReferenceCode).IsUnique().HasDatabaseName("idx_orders_reference_code");
            // Filter: list endpoint filters by status without scanning every order row
            b.HasIndex(x => x.Status).HasDatabaseName("idx_orders_status");
            // Filter: list endpoint will support filtering by customer without scanning all orders.
            b.HasIndex(x => x.CustomerId).HasDatabaseName("idx_orders_customer_id");
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

        modelBuilder.Entity<AllocationEvent>(b =>
        {
            b.ToTable("allocation_events");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasColumnName("id").HasDefaultValueSql("gen_random_uuid()");
            b.Property(x => x.EventType).HasColumnName("event_type").HasMaxLength(64).IsRequired()
                .HasConversion(
                    v => v.ToDbString(),
                    v => AllocationEventTypeExtensions.FromDbString(v));
            b.Property(x => x.OrderId).HasColumnName("order_id").IsRequired();
            b.Property(x => x.OrderLineId).HasColumnName("order_line_id").IsRequired();
            b.Property(x => x.SkuId).HasColumnName("sku_id").IsRequired();
            b.Property(x => x.Quantity).HasColumnName("quantity").IsRequired();
            b.Property(x => x.OccurredAt).HasColumnName("occurred_at").IsRequired();

            // RESTRICT: prevents deleting an order or line that has audit history.
            // This is intentional — the ledger is immutable; orders are never hard-deleted in this system.
            b.HasOne<Order>().WithMany().HasForeignKey(x => x.OrderId)
                .OnDelete(DeleteBehavior.Restrict);
            b.HasOne<OrderLine>().WithMany().HasForeignKey(x => x.OrderLineId)
                .OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Sku>().WithMany().HasForeignKey(x => x.SkuId)
                .OnDelete(DeleteBehavior.Restrict);

            // Primary query pattern: load the full event history for one order
            b.HasIndex(x => x.OrderId).HasDatabaseName("idx_allocation_events_order_id");
            // Secondary: audit queries filtering by SKU across all orders
            b.HasIndex(x => x.SkuId).HasDatabaseName("idx_allocation_events_sku_id");
            // Chronological reporting queries
            b.HasIndex(x => x.OccurredAt).HasDatabaseName("idx_allocation_events_occurred_at");
        });

        modelBuilder.Entity<OutboxMessage>(b =>
        {
            b.ToTable("outbox_messages");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasColumnName("id").HasDefaultValueSql("gen_random_uuid()");
            b.Property(x => x.EventType).HasColumnName("event_type").HasMaxLength(128).IsRequired();
            b.Property(x => x.Payload).HasColumnName("payload").HasColumnType("jsonb").IsRequired();
            b.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
            b.Property(x => x.ProcessedAt).HasColumnName("processed_at");
            b.Property(x => x.Error).HasColumnName("error");

            // The relay query fetches only unprocessed rows, ordered by creation time.
            // A partial index on WHERE processed_at IS NULL keeps this query fast as
            // the processed row count grows — processed rows are excluded from the index entirely.
            b.HasIndex(x => x.CreatedAt)
                .HasFilter("processed_at IS NULL")
                .HasDatabaseName("idx_outbox_messages_unprocessed");
        });

        modelBuilder.Entity<IdempotencyRecord>(b =>
        {
            b.ToTable("idempotency_keys");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasColumnName("id").HasDefaultValueSql("gen_random_uuid()");
            b.Property(x => x.IdempotencyKey).HasColumnName("idempotency_key").HasMaxLength(128).IsRequired();
            b.Property(x => x.RequestPath).HasColumnName("request_path").HasMaxLength(500).IsRequired();
            b.Property(x => x.RequestMethod).HasColumnName("request_method").HasMaxLength(10).IsRequired();
            b.Property(x => x.Status).HasColumnName("status").HasMaxLength(32).IsRequired();
            b.Property(x => x.ResponseStatus).HasColumnName("response_status");
            b.Property(x => x.ResponseBody).HasColumnName("response_body").HasColumnType("text");
            b.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
            b.Property(x => x.ExpiresAt).HasColumnName("expires_at").IsRequired();

            // The key lookup and uniqueness constraint — the primary query pattern.
            b.HasIndex(x => x.IdempotencyKey)
                .IsUnique()
                .HasDatabaseName("idx_idempotency_keys_key");

            // Cleanup job scans by expires_at.
            b.HasIndex(x => x.ExpiresAt)
                .HasDatabaseName("idx_idempotency_keys_expiry");
        });

        modelBuilder.Entity<AllocationRun>(b =>
        {
            b.ToTable("allocation_runs");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasColumnName("id").HasDefaultValueSql("gen_random_uuid()");
            b.Property(x => x.Status).HasColumnName("status").HasMaxLength(32).IsRequired();
            b.Property(x => x.RequestedAt).HasColumnName("requested_at").IsRequired();
            b.Property(x => x.StartedAt).HasColumnName("started_at");
            b.Property(x => x.CompletedAt).HasColumnName("completed_at");
            b.Property(x => x.RequestedBy).HasColumnName("requested_by").HasMaxLength(256);
            b.Property(x => x.Error).HasColumnName("error");
            b.Property(x => x.OrdersProcessed).HasColumnName("orders_processed");
            b.Property(x => x.OrdersFullyAllocated).HasColumnName("orders_fully_allocated");
            b.Property(x => x.OrdersPartiallyAllocated).HasColumnName("orders_partially_allocated");
            b.Property(x => x.Results).HasColumnName("results").HasColumnType("text");

            // Worker query: SELECT pending runs ordered by submission time.
            b.HasIndex(x => new { x.Status, x.RequestedAt })
                .HasDatabaseName("idx_allocation_runs_status_requested_at");
        });

        modelBuilder.Entity<Customer>(b =>
        {
            b.ToTable("customers");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasColumnName("id").HasDefaultValueSql("gen_random_uuid()");
            b.Property(x => x.CustomerCode).HasColumnName("customer_code").HasMaxLength(64).IsRequired();
            b.Property(x => x.Name).HasColumnName("name").HasMaxLength(256).IsRequired();
            b.Property(x => x.Tier).HasColumnName("tier").HasMaxLength(32).IsRequired()
                .HasConversion(
                    v => v.ToDbString(),
                    v => CustomerTierExtensions.FromDbString(v));
            b.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();

            b.HasIndex(x => x.CustomerCode).IsUnique().HasDatabaseName("idx_customers_code");
        });

        modelBuilder.Entity<CustomerContract>(b =>
        {
            b.ToTable("customer_contracts");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasColumnName("id").HasDefaultValueSql("gen_random_uuid()");
            b.Property(x => x.CustomerId).HasColumnName("customer_id").IsRequired();
            b.Property(x => x.SkuId).HasColumnName("sku_id").IsRequired();
            b.Property(x => x.FloorQty).HasColumnName("floor_qty").IsRequired();
            b.Property(x => x.CeilingQty).HasColumnName("ceiling_qty");
            b.Property(x => x.EffectiveFrom).HasColumnName("effective_from").IsRequired()
                .HasConversion(v => v.ToDateTime(TimeOnly.MinValue), v => DateOnly.FromDateTime(v));
            b.Property(x => x.EffectiveTo).HasColumnName("effective_to")
                .HasConversion(
                    v => v.HasValue ? (DateTime?)v.Value.ToDateTime(TimeOnly.MinValue) : null,
                    v => v.HasValue ? DateOnly.FromDateTime(v.Value) : null);
            b.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();

            b.HasOne(x => x.Customer).WithMany().HasForeignKey(x => x.CustomerId)
                .OnDelete(DeleteBehavior.Restrict);
            b.HasOne(x => x.Sku).WithMany().HasForeignKey(x => x.SkuId)
                .OnDelete(DeleteBehavior.Restrict);

            // One active contract per (customer, SKU) at any time is not enforced at the DB level
            // because overlapping period checks require a trigger or exclusion constraint. Phase 17
            // enforces this in the service layer — see Step 2b.
            b.HasIndex(x => new { x.CustomerId, x.SkuId })
                .HasDatabaseName("idx_customer_contracts_customer_sku");
        });

        modelBuilder.Entity<Lot>(b =>
        {
            b.ToTable("lots", t =>
            {
                t.HasCheckConstraint("chk_lots_quantity_positive",       "quantity > 0");
                t.HasCheckConstraint("chk_lots_available_non_negative",  "available_qty >= 0");
                t.HasCheckConstraint("chk_lots_available_lte_quantity",  "available_qty <= quantity");
            });
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasColumnName("id").HasDefaultValueSql("gen_random_uuid()");
            b.Property(x => x.SkuId).HasColumnName("sku_id").IsRequired();
            b.Property(x => x.LotCode).HasColumnName("lot_code").HasMaxLength(64).IsRequired();
            b.Property(x => x.Quantity).HasColumnName("quantity").IsRequired();
            b.Property(x => x.AvailableQty).HasColumnName("available_qty").IsRequired();
            b.Property(x => x.Status).HasColumnName("status").HasMaxLength(32).IsRequired()
                .HasConversion(
                    v => v.ToDbString(),
                    v => LotStatusExtensions.FromDbString(v));
            b.Property(x => x.ReceivedAt).HasColumnName("received_at").IsRequired();
            b.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();

            b.HasOne(x => x.Sku)
            .WithMany(x => x.Lots)
            .HasForeignKey(x => x.SkuId)
            .OnDelete(DeleteBehavior.Restrict);

            // Uniqueness: lot_code is the physical batch identifier — must be unique across the system.
            b.HasIndex(x => x.LotCode).IsUnique().HasDatabaseName("idx_lots_lot_code");

            // Phase 20's FIFO lot selection query: WHERE sku_id = @skuId AND status = 'available'
            // ORDER BY received_at ASC. This composite index serves that query without a sort step.
            b.HasIndex(x => new { x.SkuId, x.Status, x.ReceivedAt })
            .HasDatabaseName("idx_lots_sku_status_received");
        });

        modelBuilder.Entity<LotEvent>(b =>
        {
            b.ToTable("lot_events");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasColumnName("id").HasDefaultValueSql("gen_random_uuid()");
            b.Property(x => x.LotId).HasColumnName("lot_id").IsRequired();
            b.Property(x => x.SkuId).HasColumnName("sku_id").IsRequired();
            b.Property(x => x.EventType).HasColumnName("event_type").HasMaxLength(32).IsRequired()
                .HasConversion(
                    v => v.ToDbString(),
                    v => LotEventTypeExtensions.FromDbString(v));
            b.Property(x => x.QuantityAffected).HasColumnName("quantity_affected").IsRequired();
            b.Property(x => x.Notes).HasColumnName("notes").HasMaxLength(500);
            b.Property(x => x.OccurredAt).HasColumnName("occurred_at").IsRequired();

            b.HasOne<Lot>().WithMany().HasForeignKey(x => x.LotId)
                .OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Sku>().WithMany().HasForeignKey(x => x.SkuId)
                .OnDelete(DeleteBehavior.Restrict);

            // Primary query pattern: load all events for a given lot (Phase 21 traceability endpoint).
            b.HasIndex(x => x.LotId).HasDatabaseName("idx_lot_events_lot_id");
            // Secondary: audit queries across all lots for a single SKU.
            b.HasIndex(x => x.SkuId).HasDatabaseName("idx_lot_events_sku_id");
        });
    }
}