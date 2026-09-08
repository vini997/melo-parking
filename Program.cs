using EstacionamentoAPI.Data;
using EstacionamentoAPI.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using System.Text;
using Microsoft.AspNetCore.Identity;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(
        builder.Configuration.GetConnectionString("DefaultConnection")));
var jwtKey = builder.Configuration["Jwt:Key"]
    ?? throw new InvalidOperationException("A chave JWT não foi configurada.");

var jwtIssuer = builder.Configuration["Jwt:Issuer"];
var jwtAudience = builder.Configuration["Jwt:Audience"];

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = jwtIssuer,
            ValidAudience = jwtAudience,
            IssuerSigningKey = new SymmetricSecurityKey(
                Encoding.UTF8.GetBytes(jwtKey)),
            ClockSkew = TimeSpan.Zero
        };
    });

builder.Services.AddAuthorization(options =>
{
    options.FallbackPolicy = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build();
});

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var banco = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await banco.Database.MigrateAsync();

    var adminEmail = app.Configuration["Admin:Email"];
    var adminPassword = app.Configuration["Admin:Password"];

    if (!string.IsNullOrWhiteSpace(adminEmail) &&
        !string.IsNullOrWhiteSpace(adminPassword))
    {
        var usuarioExistente = await banco.Usuarios
            .FirstOrDefaultAsync(u => u.Email == adminEmail);

        if (usuarioExistente is null)
        {
            var administrador = new Usuario
            {
                Nome = "Administrador",
                Email = adminEmail
            };

            var passwordHasher = new PasswordHasher<Usuario>();

            administrador.SenhaHash = passwordHasher.HashPassword(
                administrador,
                adminPassword);

            banco.Usuarios.Add(administrador);
            await banco.SaveChangesAsync();

            Console.WriteLine("Administrador criado com sucesso.");
        }
    }
}

app.UseDefaultFiles();
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapGet("/veiculos", async (string? busca, AppDbContext banco) =>
{
    var consulta = banco.Veiculos
        .Include(v => v.Cliente)
        .AsQueryable();

    if (!string.IsNullOrWhiteSpace(busca))
    {
        var termo = $"%{busca.Trim()}%";

        consulta = consulta.Where(v =>
            EF.Functions.ILike(v.Placa, termo) ||
            EF.Functions.ILike(v.Marca, termo) ||
            EF.Functions.ILike(v.Cor, termo) ||
            (
                v.Cliente != null &&
                (
                    EF.Functions.ILike(v.Cliente.Nome, termo) ||
                    EF.Functions.ILike(v.Cliente.Telefone, termo) ||
                    EF.Functions.ILike(v.Cliente.Email, termo)
                )
            )
        );
    }

    var veiculos = await consulta
        .OrderByDescending(v => v.DataEntrada)
        .ToListAsync();

    return Results.Ok(veiculos);
});

app.MapGet("/veiculos/{id:int}", async (int id, AppDbContext banco) =>
{
    var veiculo = await banco.Veiculos
        .Include(v => v.Cliente)
        .FirstOrDefaultAsync(v => v.Id == id);

    return veiculo is null
        ? Results.NotFound("Veículo não encontrado.")
        : Results.Ok(veiculo);
});

app.MapPost("/veiculos", async (Veiculo novoVeiculo, AppDbContext banco) =>
{
    if (string.IsNullOrWhiteSpace(novoVeiculo.Placa))
    {
        return Results.BadRequest("A placa é obrigatória.");
    }

    novoVeiculo.Placa = novoVeiculo.Placa.Trim().ToUpper();

    var placaJaEstaEstacionada = await banco.Veiculos
    .AnyAsync(v =>
        v.Placa == novoVeiculo.Placa &&
        v.Ativo
    );

    if (placaJaEstaEstacionada)
{
    return Results.BadRequest(
        "Este veículo já está estacionado."
    );
}

    novoVeiculo.Id = 0;
    novoVeiculo.DataEntrada = DateTime.UtcNow;
    novoVeiculo.Ativo = true;

    banco.Veiculos.Add(novoVeiculo);
    await banco.SaveChangesAsync();

    return Results.Created($"/veiculos/{novoVeiculo.Id}", novoVeiculo);
});

app.MapPost(
    "/veiculos/{id:int}/saida",
    async (int id, SaidaRequest dados, AppDbContext banco) =>
    {
        var veiculo = await banco.Veiculos.FindAsync(id);

        if (veiculo is null)
        {
            return Results.NotFound("Veículo não encontrado.");
        }

        if (!veiculo.Ativo)
        {
            return Results.BadRequest(
                "A saída deste veículo já foi registrada."
            );
        }

        var formaPagamento = dados.FormaPagamento.Trim().ToLower();

        var formaPagamentoValida = formaPagamento switch
        {
            "dinheiro" => "Dinheiro",
            "cartao" => "Cartao",
            "zelle" => "Zelle",
            "outro" => "Outro",
            _ => null
        };

        if (formaPagamentoValida is null)
        {
            return Results.BadRequest(
                "Use Dinheiro, Cartao, Zelle ou Outro."
            );
        }

        var dataSaida = DateTime.UtcNow;
        var tempoEstacionado = dataSaida - veiculo.DataEntrada;

        var horasCobradas = Math.Max(
            1,
            (int)Math.Ceiling(tempoEstacionado.TotalHours)
        );

        const decimal valorPorHora = 10.00m;
        var totalPagar = horasCobradas * valorPorHora;

        veiculo.DataSaida = dataSaida;
        veiculo.ValorPago = totalPagar;
        veiculo.FormaPagamento = formaPagamentoValida;
        veiculo.Ativo = false;

        await banco.SaveChangesAsync();

        return Results.Ok(new
        {
            veiculo.Id,
            veiculo.Placa,
            veiculo.DataEntrada,
            veiculo.DataSaida,
            HorasCobradas = horasCobradas,
            ValorPorHora = valorPorHora,
            TotalPagar = totalPagar,
            FormaPagamento = formaPagamentoValida
        });
    });

app.MapGet("/clientes", async (AppDbContext banco) =>
{
    var clientes = await banco.Clientes
        .OrderBy(c => c.Nome)
        .ToListAsync();

    return Results.Ok(clientes);
});

app.MapGet("/clientes/{id:int}", async (int id, AppDbContext banco) =>
{
    var cliente = await banco.Clientes.FindAsync(id);

    return cliente is null
        ? Results.NotFound("Cliente não encontrado.")
        : Results.Ok(cliente);
});

app.MapPost("/clientes", async (Cliente novoCliente, AppDbContext banco) =>
{
    if (string.IsNullOrWhiteSpace(novoCliente.Nome))
    {
        return Results.BadRequest("O nome do cliente é obrigatório.");
    }

    novoCliente.Nome = novoCliente.Nome.Trim();
    novoCliente.Telefone = novoCliente.Telefone.Trim();
    novoCliente.Email = novoCliente.Email.Trim().ToLower();

    if (!string.IsNullOrWhiteSpace(novoCliente.Email))
    {
        var emailExiste = await banco.Clientes
            .AnyAsync(c => c.Email == novoCliente.Email);

        if (emailExiste)
        {
            return Results.BadRequest("Já existe um cliente com esse e-mail.");
        }
    }

    novoCliente.Id = 0;

    banco.Clientes.Add(novoCliente);
    await banco.SaveChangesAsync();

    return Results.Created($"/clientes/{novoCliente.Id}", novoCliente);
});

app.MapPatch(
    "/veiculos/{veiculoId:int}/cliente/{clienteId:int}",
    async (int veiculoId, int clienteId, AppDbContext banco) =>
    {
        var veiculo = await banco.Veiculos.FindAsync(veiculoId);

        if (veiculo is null)
        {
            return Results.NotFound("Veículo não encontrado.");
        }

        var cliente = await banco.Clientes.FindAsync(clienteId);

        if (cliente is null)
        {
            return Results.NotFound("Cliente não encontrado.");
        }

        veiculo.ClienteId = cliente.Id;

        await banco.SaveChangesAsync();

        return Results.Ok(new
        {
            Mensagem = "Veículo associado ao cliente com sucesso.",
            VeiculoId = veiculo.Id,
            veiculo.Placa,
            ClienteId = cliente.Id,
            cliente.Nome
        });
    });

app.MapGet("/relatorios/diario", async (AppDbContext banco) =>
{
    var fusoMiami = TimeZoneInfo.FindSystemTimeZoneById(
        "America/New_York"
    );

    var agoraEmMiami = TimeZoneInfo.ConvertTimeFromUtc(
        DateTime.UtcNow,
        fusoMiami
    );

    var inicioLocal = DateTime.SpecifyKind(
        agoraEmMiami.Date,
        DateTimeKind.Unspecified
    );

    var fimLocal = inicioLocal.AddDays(1);

    var inicioUtc = TimeZoneInfo.ConvertTimeToUtc(
        inicioLocal,
        fusoMiami
    );

    var fimUtc = TimeZoneInfo.ConvertTimeToUtc(
        fimLocal,
        fusoMiami
    );

    var entradas = await banco.Veiculos
        .Where(v =>
            v.DataEntrada >= inicioUtc &&
            v.DataEntrada < fimUtc)
        .ToListAsync();

    var saidas = await banco.Veiculos
        .Where(v =>
            v.DataSaida.HasValue &&
            v.DataSaida.Value >= inicioUtc &&
            v.DataSaida.Value < fimUtc)
        .ToListAsync();

    decimal Total(string forma) =>
        saidas
            .Where(v => v.FormaPagamento == forma)
            .Sum(v => v.ValorPago ?? 0);

    int Quantidade(string forma) =>
        saidas.Count(v => v.FormaPagamento == forma);

    return Results.Ok(new
    {
        Data = agoraEmMiami.ToString("yyyy-MM-dd"),
        EntradasHoje = entradas.Count,
        SaidasHoje = saidas.Count,
        EstacionadosAgora = await banco.Veiculos
            .CountAsync(v => v.Ativo),

        TotalRecebidoHoje = saidas
            .Sum(v => v.ValorPago ?? 0),

        RecebimentosPorForma = new
        {
            Dinheiro = Total("Dinheiro"),
            Cartao = Total("Cartao"),
            Zelle = Total("Zelle"),
            Outro = Total("Outro"),
            NaoInformado = saidas
                .Where(v => string.IsNullOrEmpty(v.FormaPagamento))
                .Sum(v => v.ValorPago ?? 0)
        },

        QuantidadePorForma = new
        {
            Dinheiro = Quantidade("Dinheiro"),
            Cartao = Quantidade("Cartao"),
            Zelle = Quantidade("Zelle"),
            Outro = Quantidade("Outro"),
            NaoInformado = saidas.Count(
                v => string.IsNullOrEmpty(v.FormaPagamento)
            )
        },

        VeiculosQueEntraram = entradas,
        VeiculosQueSairam = saidas
    });
});

app.MapPost("/login", async (
    LoginRequest dados,
    AppDbContext banco,
    IConfiguration configuration) =>
{
    var email = dados.Email.Trim().ToLower();

    var usuario = await banco.Usuarios
        .FirstOrDefaultAsync(u => u.Email.ToLower() == email);

    if (usuario is null)
    {
        return Results.Unauthorized();
    }

    var passwordHasher = new PasswordHasher<Usuario>();

    var resultado = passwordHasher.VerifyHashedPassword(
        usuario,
        usuario.SenhaHash,
        dados.Senha);

    if (resultado == PasswordVerificationResult.Failed)
    {
        return Results.Unauthorized();
    }

    var claims = new[]
    {
        new Claim(JwtRegisteredClaimNames.Sub, usuario.Id.ToString()),
        new Claim(JwtRegisteredClaimNames.Email, usuario.Email),
        new Claim(ClaimTypes.Name, usuario.Nome),
        new Claim(ClaimTypes.Role, "Administrador")
    };

    var chave = new SymmetricSecurityKey(
        Encoding.UTF8.GetBytes(configuration["Jwt:Key"]!));

    var credenciais = new SigningCredentials(
        chave,
        SecurityAlgorithms.HmacSha256);

    var token = new JwtSecurityToken(
        issuer: configuration["Jwt:Issuer"],
        audience: configuration["Jwt:Audience"],
        claims: claims,
        expires: DateTime.UtcNow.AddHours(8),
        signingCredentials: credenciais);

    return Results.Ok(new
    {
        token = new JwtSecurityTokenHandler().WriteToken(token),
        usuario = new
        {
            usuario.Id,
            usuario.Nome,
            usuario.Email
        }
    });
}).AllowAnonymous();

app.Run();