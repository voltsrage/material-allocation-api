using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MaterialAllocationApi.Migrations
{
    /// <inheritdoc />
    public partial class AddLots : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "lots",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    sku_id = table.Column<Guid>(type: "uuid", nullable: false),
                    lot_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    quantity = table.Column<int>(type: "integer", nullable: false),
                    available_qty = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_lots", x => x.id);
                    table.CheckConstraint("chk_lots_available_lte_quantity", "available_qty <= quantity");
                    table.CheckConstraint("chk_lots_available_non_negative", "available_qty >= 0");
                    table.CheckConstraint("chk_lots_quantity_positive", "quantity > 0");
                    table.ForeignKey(
                        name: "FK_lots_skus_sku_id",
                        column: x => x.sku_id,
                        principalTable: "skus",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "idx_lots_lot_code",
                table: "lots",
                column: "lot_code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "idx_lots_sku_status_received",
                table: "lots",
                columns: new[] { "sku_id", "status", "received_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "lots");
        }
    }
}
