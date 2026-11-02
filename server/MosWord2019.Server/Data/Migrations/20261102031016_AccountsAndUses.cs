using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MosWord2019.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AccountsAndUses : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "AccountCredentialVersion",
                table: "Sessions",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "CredentialVersion",
                table: "Accounts",
                type: "uuid",
                nullable: false,
                defaultValueSql: "gen_random_uuid()");

            migrationBuilder.AddColumn<int>(
                name: "FailedLoginCount",
                table: "Accounts",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LockoutUntil",
                table: "Accounts",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_Accounts_Failures",
                table: "Accounts",
                sql: "\"FailedLoginCount\" BETWEEN 0 AND 10");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_Accounts_Failures",
                table: "Accounts");

            migrationBuilder.DropColumn(
                name: "AccountCredentialVersion",
                table: "Sessions");

            migrationBuilder.DropColumn(
                name: "CredentialVersion",
                table: "Accounts");

            migrationBuilder.DropColumn(
                name: "FailedLoginCount",
                table: "Accounts");

            migrationBuilder.DropColumn(
                name: "LockoutUntil",
                table: "Accounts");
        }
    }
}
