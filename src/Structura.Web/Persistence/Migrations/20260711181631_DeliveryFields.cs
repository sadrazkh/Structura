using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Structura.Web.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DeliveryFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "delivery_external_id",
                table: "records",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "delivery_next_retry_at",
                table: "records",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_records_project_id_review_status_delivery_status",
                table: "records",
                columns: new[] { "project_id", "review_status", "delivery_status" });

            migrationBuilder.CreateIndex(
                name: "ix_records_reviewed_by_id",
                table: "records",
                column: "reviewed_by_id");

            migrationBuilder.AddForeignKey(
                name: "fk_records_users_reviewed_by_id",
                table: "records",
                column: "reviewed_by_id",
                principalTable: "users",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_records_users_reviewed_by_id",
                table: "records");

            migrationBuilder.DropIndex(
                name: "ix_records_project_id_review_status_delivery_status",
                table: "records");

            migrationBuilder.DropIndex(
                name: "ix_records_reviewed_by_id",
                table: "records");

            migrationBuilder.DropColumn(
                name: "delivery_external_id",
                table: "records");

            migrationBuilder.DropColumn(
                name: "delivery_next_retry_at",
                table: "records");
        }
    }
}
