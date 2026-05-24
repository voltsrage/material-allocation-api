using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MaterialAllocationApi.Migrations
{
    /// <inheritdoc />
    public partial class InitialSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "skus",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    sku_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    on_hand = table.Column<int>(type: "integer", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_skus", x => x.id);
                    table.CheckConstraint("chk_skus_on_hand_non_negative", "on_hand >= 0");
                });

            migrationBuilder.CreateTable(
                name: "inventory_adjustments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    sku_id = table.Column<Guid>(type: "uuid", nullable: false),
                    delta = table.Column<int>(type: "integer", nullable: false),
                    reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_inventory_adjustments", x => x.id);
                    table.ForeignKey(
                        name: "FK_inventory_adjustments_skus_sku_id",
                        column: x => x.sku_id,
                        principalTable: "skus",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "idx_adjustments_sku_id",
                table: "inventory_adjustments",
                column: "sku_id");

            migrationBuilder.CreateIndex(
                name: "idx_skus_sku_code",
                table: "skus",
                column: "sku_code",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "inventory_adjustments");

            migrationBuilder.DropTable(
                name: "skus");
        }
    }
}
