using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MaterialAllocationApi.Migrations
{
    /// <inheritdoc />
    public partial class AddLotIdToAllocationEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "lot_id",
                table: "allocation_events",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "idx_allocation_events_lot_id",
                table: "allocation_events",
                column: "lot_id",
                filter: "lot_id IS NOT NULL");

            migrationBuilder.AddForeignKey(
                name: "FK_allocation_events_lots_lot_id",
                table: "allocation_events",
                column: "lot_id",
                principalTable: "lots",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_allocation_events_lots_lot_id",
                table: "allocation_events");

            migrationBuilder.DropIndex(
                name: "idx_allocation_events_lot_id",
                table: "allocation_events");

            migrationBuilder.DropColumn(
                name: "lot_id",
                table: "allocation_events");
        }
    }
}
