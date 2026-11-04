using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ControlCenter.Api.Dados.Migrations
{
    /// <inheritdoc />
    public partial class Inicial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Comandos",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    QuandoUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Usuario = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Acao = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Alvo = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    Dados = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    Sucesso = table.Column<bool>(type: "INTEGER", nullable: false),
                    Erro = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    DuracaoMs = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Comandos", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Comandos_Acao",
                table: "Comandos",
                column: "Acao");

            migrationBuilder.CreateIndex(
                name: "IX_Comandos_QuandoUtc",
                table: "Comandos",
                column: "QuandoUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Comandos");
        }
    }
}
