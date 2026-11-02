using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace MosWord2019.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class HeartbeatHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "TestingClockBaselineMilliseconds",
                table: "Sessions",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "TestingClockStartedAt",
                table: "Sessions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "TimingInconsistent",
                table: "Sessions",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "Heartbeats",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SessionId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReceivedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ClientElapsedMilliseconds = table.Column<long>(type: "bigint", nullable: true),
                    WasOffline = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Heartbeats", x => x.Id);
                    table.CheckConstraint("CK_Heartbeats_Elapsed", "\"ClientElapsedMilliseconds\" IS NULL OR \"ClientElapsedMilliseconds\" BETWEEN 0 AND 86400000");
                    table.ForeignKey(
                        name: "FK_Heartbeats_Sessions_SessionId",
                        column: x => x.SessionId,
                        principalTable: "Sessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "OfflineGaps",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SessionId = table.Column<Guid>(type: "uuid", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    EndedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OfflineGaps", x => x.Id);
                    table.CheckConstraint("CK_OfflineGaps_Kind", "\"Kind\" IN ('Network', 'Unobserved')");
                    table.CheckConstraint("CK_OfflineGaps_Time", "\"EndedAt\" >= \"StartedAt\"");
                    table.ForeignKey(
                        name: "FK_OfflineGaps_Sessions_SessionId",
                        column: x => x.SessionId,
                        principalTable: "Sessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Heartbeats_SessionId_ReceivedAt",
                table: "Heartbeats",
                columns: new[] { "SessionId", "ReceivedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_OfflineGaps_SessionId_StartedAt",
                table: "OfflineGaps",
                columns: new[] { "SessionId", "StartedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Heartbeats");

            migrationBuilder.DropTable(
                name: "OfflineGaps");

            migrationBuilder.DropColumn(
                name: "TestingClockBaselineMilliseconds",
                table: "Sessions");

            migrationBuilder.DropColumn(
                name: "TestingClockStartedAt",
                table: "Sessions");

            migrationBuilder.DropColumn(
                name: "TimingInconsistent",
                table: "Sessions");
        }
    }
}
