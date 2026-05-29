using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MaterialAllocationApi.Migrations
{
    /// <inheritdoc />
    public partial class AddLotEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "lot_events",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    lot_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sku_id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    quantity_affected = table.Column<int>(type: "integer", nullable: false),
                    notes = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_lot_events", x => x.id);
                    table.ForeignKey(
                        name: "FK_lot_events_lots_lot_id",
                        column: x => x.lot_id,
                        principalTable: "lots",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_lot_events_skus_sku_id",
                        column: x => x.sku_id,
                        principalTable: "skus",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "idx_lot_events_lot_id",
                table: "lot_events",
                column: "lot_id");

            migrationBuilder.CreateIndex(
                name: "idx_lot_events_sku_id",
                table: "lot_events",
                column: "sku_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "lot_events");
        }
    }
}
