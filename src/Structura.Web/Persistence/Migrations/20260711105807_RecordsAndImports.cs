using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Structura.Web.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RecordsAndImports : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "import_runs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    file_name = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    file_path = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    mapping = table.Column<string>(type: "jsonb", nullable: true),
                    status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    total_rows = table.Column<int>(type: "integer", nullable: true),
                    imported = table.Column<int>(type: "integer", nullable: false),
                    skipped_duplicates = table.Column<int>(type: "integer", nullable: false),
                    failed = table.Column<int>(type: "integer", nullable: false),
                    errors = table.Column<string>(type: "jsonb", nullable: false),
                    last_row_processed = table.Column<int>(type: "integer", nullable: false),
                    created_by_id = table.Column<Guid>(type: "uuid", nullable: false),
                    finished_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_import_runs", x => x.id);
                    table.CheckConstraint("ck_import_runs_source", "source IN ('Excel','Csv','Manual','Api')");
                    table.CheckConstraint("ck_import_runs_status", "status IN ('AwaitingMapping','Running','Completed','CompletedWithErrors','Failed','Cancelled')");
                    table.ForeignKey(
                        name: "fk_import_runs_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "processing_runs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    schema_snapshot = table.Column<string>(type: "jsonb", nullable: false),
                    prompt_snapshot = table.Column<string>(type: "jsonb", nullable: false),
                    model = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    total = table.Column<int>(type: "integer", nullable: false),
                    succeeded = table.Column<int>(type: "integer", nullable: false),
                    failed = table.Column<int>(type: "integer", nullable: false),
                    cancel_requested = table.Column<bool>(type: "boolean", nullable: false),
                    input_tokens = table.Column<long>(type: "bigint", nullable: false),
                    output_tokens = table.Column<long>(type: "bigint", nullable: false),
                    created_by_id = table.Column<Guid>(type: "uuid", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    finished_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_processing_runs", x => x.id);
                    table.CheckConstraint("ck_processing_runs_status", "status IN ('Running','Completed','CompletedWithErrors','Cancelled','Failed')");
                    table.ForeignKey(
                        name: "fk_processing_runs_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "records",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    external_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    text = table.Column<string>(type: "text", nullable: false),
                    processing_status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    review_status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    delivery_status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    processing_error = table.Column<string>(type: "text", nullable: true),
                    latest_result_id = table.Column<Guid>(type: "uuid", nullable: true),
                    processing_run_id = table.Column<Guid>(type: "uuid", nullable: true),
                    import_run_id = table.Column<Guid>(type: "uuid", nullable: true),
                    assigned_reviewer_id = table.Column<Guid>(type: "uuid", nullable: true),
                    assigned_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    final_output = table.Column<string>(type: "jsonb", nullable: true),
                    reviewed_by_id = table.Column<Guid>(type: "uuid", nullable: true),
                    reviewed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    review_note = table.Column<string>(type: "text", nullable: true),
                    delivery_attempts = table.Column<int>(type: "integer", nullable: false),
                    delivered_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    delivery_error = table.Column<string>(type: "text", nullable: true),
                    version = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_records", x => x.id);
                    table.CheckConstraint("ck_records_delivery_status", "delivery_status IN ('Pending','Delivered','Failed')");
                    table.CheckConstraint("ck_records_processing_status", "processing_status IN ('Pending','Processing','Completed','Failed')");
                    table.CheckConstraint("ck_records_review_status", "review_status IN ('Unassigned','Assigned','InReview','Approved','Rejected','ReprocessRequested')");
                    table.ForeignKey(
                        name: "fk_records_import_runs_import_run_id",
                        column: x => x.import_run_id,
                        principalTable: "import_runs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_records_processing_runs_processing_run_id",
                        column: x => x.processing_run_id,
                        principalTable: "processing_runs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_records_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_records_users_assigned_reviewer_id",
                        column: x => x.assigned_reviewer_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "extraction_results",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    record_id = table.Column<Guid>(type: "uuid", nullable: false),
                    run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    model = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    raw_response = table.Column<string>(type: "text", nullable: true),
                    output = table.Column<string>(type: "jsonb", nullable: true),
                    error = table.Column<string>(type: "text", nullable: true),
                    input_tokens = table.Column<int>(type: "integer", nullable: false),
                    output_tokens = table.Column<int>(type: "integer", nullable: false),
                    duration_ms = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_extraction_results", x => x.id);
                    table.CheckConstraint("ck_extraction_results_status", "status IN ('Succeeded','Failed')");
                    table.ForeignKey(
                        name: "fk_extraction_results_processing_runs_run_id",
                        column: x => x.run_id,
                        principalTable: "processing_runs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_extraction_results_records_record_id",
                        column: x => x.record_id,
                        principalTable: "records",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_extraction_results_record_id_created_at",
                table: "extraction_results",
                columns: new[] { "record_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_extraction_results_run_id",
                table: "extraction_results",
                column: "run_id");

            migrationBuilder.CreateIndex(
                name: "ix_import_runs_project_id_created_at",
                table: "import_runs",
                columns: new[] { "project_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_processing_runs_project_id_created_at",
                table: "processing_runs",
                columns: new[] { "project_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_processing_runs_status",
                table: "processing_runs",
                column: "status",
                filter: "status = 'Running'");

            migrationBuilder.CreateIndex(
                name: "ix_records_assigned_reviewer_id",
                table: "records",
                column: "assigned_reviewer_id");

            migrationBuilder.CreateIndex(
                name: "ix_records_import_run_id",
                table: "records",
                column: "import_run_id");

            migrationBuilder.CreateIndex(
                name: "ix_records_processing_run_id_processing_status",
                table: "records",
                columns: new[] { "processing_run_id", "processing_status" });

            migrationBuilder.CreateIndex(
                name: "ix_records_project_id_assigned_reviewer_id_review_status",
                table: "records",
                columns: new[] { "project_id", "assigned_reviewer_id", "review_status" });

            migrationBuilder.CreateIndex(
                name: "ix_records_project_id_delivery_status",
                table: "records",
                columns: new[] { "project_id", "delivery_status" });

            migrationBuilder.CreateIndex(
                name: "ix_records_project_id_external_id",
                table: "records",
                columns: new[] { "project_id", "external_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_records_project_id_processing_status",
                table: "records",
                columns: new[] { "project_id", "processing_status" });

            migrationBuilder.CreateIndex(
                name: "ix_records_project_id_review_status",
                table: "records",
                columns: new[] { "project_id", "review_status" });

            migrationBuilder.CreateIndex(
                name: "ix_records_project_id_updated_at",
                table: "records",
                columns: new[] { "project_id", "updated_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "extraction_results");

            migrationBuilder.DropTable(
                name: "records");

            migrationBuilder.DropTable(
                name: "import_runs");

            migrationBuilder.DropTable(
                name: "processing_runs");
        }
    }
}
