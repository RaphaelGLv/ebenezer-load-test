using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Bogus;
using EbenezerBackend.LoadTests;
using EbenezerBackend.LoadTests.Reporting;
using NBomber.Contracts;
using NBomber.Contracts.Stats;
using NBomber.CSharp;

// ---------------------------------------------------------------------------
// �nico argumento obrigat�rio: a abordagem de persist�ncia.
//   dotnet run -- multimodel
//   dotnet run -- heterogeneous
//
// O arquivo de configura��o � sempre benchmark.config.jsonc, resolvido
// relativo ao diret�rio do projeto (n�o ao diret�rio de trabalho atual).
// ---------------------------------------------------------------------------
var approachArg = args.FirstOrDefault(a => !a.StartsWith('-'));

if (string.IsNullOrWhiteSpace(approachArg))
{
    Console.Error.WriteLine(
        """
        Erro: informe a abordagem de persist�ncia sob teste.

        Uso:
          dotnet run -- <multimodel|heterogeneous>

          multimodel    -> ArangoDB (container: arangodb)
          heterogeneous -> MongoDB + Neo4j (containers: ebenezer-mongodb, ebenezer-neo4j)
        """);
    Environment.Exit(1);
    return;
}

PersistenceApproach approach;
try
{
    approach = PersistenceApproach.Parse(approachArg);
}
catch (ArgumentException ex)
{
    Console.Error.WriteLine($"Erro: {ex.Message}");
    Environment.Exit(1);
    return;
}

// Resolve benchmark.config.jsonc relativo ao diret�rio do projeto,
// independentemente do diret�rio de trabalho a partir do qual o script � invocado.
var projectRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", ".."));
var configPath = Path.Combine(projectRoot, "benchmark.config.jsonc");

// ---------------------------------------------------------------------------
// Carrega a configura��o simples (URL, n�vel de carga, cen�rio, recursos).
// ---------------------------------------------------------------------------
var config = LoadConfig.Load(configPath);
var profile = config.Profile;
var warmup = Math.Max(0, config.WarmupSeconds);

Console.WriteLine($"Ebenezer LoadTests | abordagem={approach.Label} | " +
                  $"baseUrl={config.BaseUrl} | nivel={profile.Name} " +
                  $"(alvo {profile.TargetRequestsPerSecond} req HTTP/s por {profile.DurationSeconds}s) | " +
                  $"cenario={config.Scenario}");

// HttpClient �nico e compartilhado (evita exaust�o de sockets em cargas altas).
var http = new HttpClient
{
    BaseAddress = new Uri(config.BaseUrl),
    Timeout = TimeSpan.FromSeconds(600)
};

// ---------------------------------------------------------------------------
// Health-check: verifica que a API est� no ar antes de iniciar qualquer carga.
// Falha r�pida evita gastar toda a dura��o acumulando timeouts.
// ---------------------------------------------------------------------------
Console.WriteLine("Verificando disponibilidade da API...");
for (var attempt = 1; attempt <= 3; attempt++)
{
    try
    {
        using var probe = new HttpRequestMessage(HttpMethod.Get, "/");
        using var probeResp = await http.SendAsync(probe);
        // Qualquer resposta HTTP (mesmo 404) significa que a API est� no ar.
        Console.WriteLine($"API respondeu com {(int)probeResp.StatusCode} � pronta.");
        break;
    }
    catch (Exception ex) when (attempt < 3)
    {
        Console.WriteLine($"Tentativa {attempt}/3 falhou ({ex.GetType().Name}). Aguardando 5s...");
        await Task.Delay(5_000);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Erro: API n�o respondeu em {config.BaseUrl} ap�s 3 tentativas. ({ex.Message})");
        http.Dispose();
        Environment.Exit(1);
        return;
    }
}

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------
static StringContent Json(object payload) =>
    new(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

static string NewUserName(Faker f)
{
    var raw = f.Internet.UserName().ToLowerInvariant() + Guid.NewGuid().ToString("N")[..6];
    var clean = new string(raw.Where(char.IsLetterOrDigit).ToArray());
    return clean[..Math.Min(20, clean.Length)];
}

static string? ReadProp(string json, string prop)
{
    try
    {
        using var doc = JsonDocument.Parse(json);
        // Suporta path simples ("id") e aninhado ("comment.id").
        var parts = prop.Split('.');
        JsonElement el = doc.RootElement;
        foreach (var part in parts)
        {
            if (!el.TryGetProperty(part, out el))
                return null;
        }
        return el.ValueKind == JsonValueKind.String ? el.GetString() : null;
    }
    catch { return null; }
}

// Faz uma requisição HTTP com Bearer opcional e retorna (sucesso, corpo, bytes, statusCode).
async Task<(bool ok, string body, int bytes, string statusCode)> Call(HttpMethod method, string path, string? token = null, object? payload = null)
{
    using var req = new HttpRequestMessage(method, path);
    if (token is not null)
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
    if (payload is not null)
        req.Content = Json(payload);

    using var resp = await http.SendAsync(req);
    var bodyBytes = await resp.Content.ReadAsByteArrayAsync();
    var body = System.Text.Encoding.UTF8.GetString(bodyBytes);

    // Loga erros HTTP no console para diagnóstico em tempo real.
    if (!resp.IsSuccessStatusCode)
        Console.WriteLine($"[FAIL] {method} {path} → {(int)resp.StatusCode} | body: {body[..Math.Min(200, body.Length)]}");

    return (resp.IsSuccessStatusCode, body, bodyBytes.Length, ((int)resp.StatusCode).ToString());
}

// Registra um usu�rio novo e devolve (userName, token, bytes, statusCode). Usado como setup em v�rios cen�rios.
async Task<(string userName, string token, int bytes, string statusCode)> Register(Faker f)
{
    var userName = NewUserName(f);
    var (ok, body, bytes, statusCode) = await Call(HttpMethod.Post, "/auth/register",
        payload: new { userName, email = f.Internet.Email(userName), password = "Teste@123" });
    return ok
        ? (userName, ReadProp(body, "token") ?? string.Empty, bytes, statusCode)
        : (userName, string.Empty, bytes, statusCode);
}

// Aplica warm-up + simula��o de carga.
// A taxa vem do ThroughputPlanner: ele converte o alvo de requests HTTP/s do n�vel de
// carga em ITERA��ES/s por cen�rio, considerando quantos requests cada itera��o dispara.
ScenarioProps Configure(ScenarioProps scenario, int iterationsPerSecond)
{
    var withWarmUp = warmup > 0
        ? scenario.WithWarmUpDuration(TimeSpan.FromSeconds(warmup))
        : scenario.WithoutWarmUp();

    return withWarmUp.WithLoadSimulations(
        Simulation.Inject(
            rate: iterationsPerSecond,
            interval: TimeSpan.FromSeconds(1),
            during: TimeSpan.FromSeconds(profile.DurationSeconds)));
}

// ---------------------------------------------------------------------------
// Tracker de steps pulados durante a execução dos cenários (Req 9.1–9.4).
// As closures dos cenários capturam esta variável lexicalmente.
// ---------------------------------------------------------------------------
var skipTracker = new SkipTracker();

// ===========================================================================
// CEN�RIO 1 � ESCRITA INTENSIVA (write-heavy)
// Exercita o caminho de grava��o: cadastro, perfil, categorias, ora��es,
// coment�rios, rea��es e exclus�es.
// Endpoints: /auth/register, /profile/new, /categories/new, PUT /categories,
//            DELETE /categories, /prayers/new, PUT /prayers, DELETE /prayers,
//            /prayers/{id}/comment, /prayers/{id}/support, /support-remove
// ===========================================================================
var writeHeavy = Scenario.Create("write_heavy", async context =>
{
    var f = new Faker("pt_BR");
    string token = string.Empty, categoryId = string.Empty, prayerId = string.Empty, userName = string.Empty;

    var s01 = await Step.Run("01_register", context, async () =>
    {
        string regStatus; int regBytes;
        (userName, token, regBytes, regStatus) = await Register(f);
        return string.IsNullOrEmpty(token)
            ? Response.Fail(statusCode: regStatus)
            : Response.Ok(statusCode: regStatus, sizeBytes: regBytes);
    });
    if (s01.IsError)
    {
        skipTracker.Record("write_heavy", "02_register_profile", "s01.IsError = true – registration failed");
        skipTracker.Record("write_heavy", "03_create_category",  "s01.IsError = true – registration failed");
        skipTracker.Record("write_heavy", "04_update_category",  "s01.IsError = true – registration failed");
        skipTracker.Record("write_heavy", "05_create_prayer",    "s01.IsError = true – registration failed");
        skipTracker.Record("write_heavy", "06_update_prayer",    "s01.IsError = true – registration failed");
        skipTracker.Record("write_heavy", "07_add_comment",      "s01.IsError = true – registration failed");
        skipTracker.Record("write_heavy", "08_support",          "s01.IsError = true – registration failed");
        skipTracker.Record("write_heavy", "09_support_remove",   "s01.IsError = true – registration failed");
        skipTracker.Record("write_heavy", "10_delete_prayer",    "s01.IsError = true – registration failed");
        skipTracker.Record("write_heavy", "11_delete_category",  "s01.IsError = true – registration failed");
        return Response.Fail();
    }

    await Step.Run("02_register_profile", context, async () =>
    {
        var (ok, _, bytes, statusCode) = await Call(HttpMethod.Post, "/profile/new", token,
            new { fullName = f.Name.FullName(), bio = f.Lorem.Sentence(), phone = f.Phone.PhoneNumber("###########") });
        return ok ? Response.Ok(statusCode: statusCode, sizeBytes: bytes) : Response.Fail(statusCode: statusCode.ToString());
    });

    await Step.Run("03_create_category", context, async () =>
    {
        var (ok, body, bytes, statusCode) = await Call(HttpMethod.Post, "/categories/new", token,
            new { name = f.Commerce.Department(), description = f.Lorem.Sentence(), colorHex = $"#{f.Random.Hexadecimal(6, "")}", isPublic = true });
        if (ok) categoryId = ReadProp(body, "id") ?? string.Empty;
        return ok ? Response.Ok(statusCode: statusCode, sizeBytes: bytes) : Response.Fail(statusCode: statusCode.ToString());
    });

    if (!string.IsNullOrEmpty(categoryId))
    {
        await Step.Run("04_update_category", context, async () =>
        {
            var (ok, _, bytes, statusCode) = await Call(HttpMethod.Put, $"/categories/{categoryId}", token,
                new { name = f.Commerce.Department(), description = f.Lorem.Sentence(), colorHex = $"#{f.Random.Hexadecimal(6, "")}", isPublic = true });
            return ok ? Response.Ok(statusCode: statusCode, sizeBytes: bytes) : Response.Fail(statusCode: statusCode.ToString());
        });
    }
    else
    {
        skipTracker.Record("write_heavy", "04_update_category", "categoryId is empty");
    }

    var s05 = await Step.Run("05_create_prayer", context, async () =>
    {
        var categoryIds = string.IsNullOrEmpty(categoryId) ? Array.Empty<string>() : new[] { categoryId };
        var (ok, body, bytes, statusCode) = await Call(HttpMethod.Post, "/prayers/new", token,
            new { content = f.Lorem.Paragraph(), isPublic = true, categoryIds });
        if (ok) prayerId = ReadProp(body, "id") ?? string.Empty;
        return ok ? Response.Ok(statusCode: statusCode, sizeBytes: bytes) : Response.Fail(statusCode: statusCode.ToString());
    });

    if (!s05.IsError && !string.IsNullOrEmpty(prayerId))
    {
        await Step.Run("06_update_prayer", context, async () =>
        {
            var (ok, _, bytes, statusCode) = await Call(HttpMethod.Put, $"/prayers/{prayerId}", token,
                new { content = f.Lorem.Paragraph(), isPublic = true, categoryIds = Array.Empty<string>() });
            return ok ? Response.Ok(statusCode: statusCode, sizeBytes: bytes) : Response.Fail(statusCode: statusCode.ToString());
        });

        string commentId = string.Empty;
        var s07 = await Step.Run("07_add_comment", context, async () =>
        {
            var (ok, body, bytes, statusCode) = await Call(HttpMethod.Post, $"/comments/prayer/{prayerId}", token,
                new { content = f.Lorem.Sentence() });
            if (ok) commentId = ReadProp(body, "comment.id") ?? string.Empty;
            return ok ? Response.Ok(statusCode: statusCode, sizeBytes: bytes) : Response.Fail(statusCode: statusCode.ToString());
        });

        if (!s07.IsError && !string.IsNullOrEmpty(commentId))
        {
            await Step.Run("07b_get_comments_of_prayer", context, async () =>
            {
                var (ok, _, bytes, statusCode) = await Call(HttpMethod.Get, $"/comments/prayer/{prayerId}?page=1&pageSize=10", token);
                return ok ? Response.Ok(statusCode: statusCode, sizeBytes: bytes) : Response.Fail(statusCode: statusCode.ToString());
            });

            await Step.Run("07c_update_comment", context, async () =>
            {
                var (ok, _, bytes, statusCode) = await Call(HttpMethod.Put, $"/comments/{commentId}", token,
                    new { content = f.Lorem.Sentence() });
                return ok ? Response.Ok(statusCode: statusCode, sizeBytes: bytes) : Response.Fail(statusCode: statusCode.ToString());
            });

            string subCommentId = string.Empty;
            var s07d = await Step.Run("07d_add_sub_comment", context, async () =>
            {
                var (ok, body, bytes, statusCode) = await Call(HttpMethod.Post, $"/comments/comment/{commentId}", token,
                    new { content = f.Lorem.Sentence() });
                if (ok) subCommentId = ReadProp(body, "comment.id") ?? string.Empty;
                return ok ? Response.Ok(statusCode: statusCode, sizeBytes: bytes) : Response.Fail(statusCode: statusCode.ToString());
            });

            if (!s07d.IsError && !string.IsNullOrEmpty(subCommentId))
            {
                await Step.Run("07e_get_sub_comments", context, async () =>
                {
                    var (ok, _, bytes, statusCode) = await Call(HttpMethod.Get, $"/comments/comment/{commentId}?page=1&pageSize=10", token);
                    return ok ? Response.Ok(statusCode: statusCode, sizeBytes: bytes) : Response.Fail(statusCode: statusCode.ToString());
                });

                await Step.Run("07f_delete_sub_comment", context, async () =>
                {
                    var (ok, _, bytes, statusCode) = await Call(HttpMethod.Delete, $"/comments/{subCommentId}", token);
                    return ok ? Response.Ok(statusCode: statusCode, sizeBytes: bytes) : Response.Fail(statusCode: statusCode.ToString());
                });
            }
            else
            {
                var subSkipReason = s07d.IsError
                    ? "s07d.IsError = true – sub-comment creation failed"
                    : "subCommentId is empty";
                skipTracker.Record("write_heavy", "07e_get_sub_comments",  subSkipReason);
                skipTracker.Record("write_heavy", "07f_delete_sub_comment", subSkipReason);
            }

            await Step.Run("07g_add_reaction_to_comment", context, async () =>
            {
                var (ok, _, bytes, statusCode) = await Call(HttpMethod.Post, $"/comments/{commentId}/support", token);
                return ok ? Response.Ok(statusCode: statusCode, sizeBytes: bytes) : Response.Fail(statusCode: statusCode.ToString());
            });

            await Step.Run("07h_remove_reaction_from_comment", context, async () =>
            {
                var (ok, _, bytes, statusCode) = await Call(HttpMethod.Post, $"/comments/{commentId}/remove-support", token);
                return ok ? Response.Ok(statusCode: statusCode, sizeBytes: bytes) : Response.Fail(statusCode: statusCode.ToString());
            });

            await Step.Run("07i_delete_comment", context, async () =>
            {
                var (ok, _, bytes, statusCode) = await Call(HttpMethod.Delete, $"/comments/{commentId}", token);
                return ok ? Response.Ok(statusCode: statusCode, sizeBytes: bytes) : Response.Fail(statusCode: statusCode.ToString());
            });
        }
        else
        {
            var commentSkipReason = s07.IsError
                ? "s07.IsError = true – comment creation failed"
                : "commentId is empty";
            skipTracker.Record("write_heavy", "07b_get_comments_of_prayer",   commentSkipReason);
            skipTracker.Record("write_heavy", "07c_update_comment",            commentSkipReason);
            skipTracker.Record("write_heavy", "07d_add_sub_comment",           commentSkipReason);
            skipTracker.Record("write_heavy", "07e_get_sub_comments",          commentSkipReason);
            skipTracker.Record("write_heavy", "07f_delete_sub_comment",        commentSkipReason);
            skipTracker.Record("write_heavy", "07g_add_reaction_to_comment",   commentSkipReason);
            skipTracker.Record("write_heavy", "07h_remove_reaction_from_comment", commentSkipReason);
            skipTracker.Record("write_heavy", "07i_delete_comment",            commentSkipReason);
        }

        await Step.Run("08_support", context, async () =>
        {
            var (ok, _, bytes, statusCode) = await Call(HttpMethod.Post, $"/prayers/{prayerId}/support", token);
            return ok ? Response.Ok(statusCode: statusCode, sizeBytes: bytes) : Response.Fail(statusCode: statusCode.ToString());
        });

        await Step.Run("09_support_remove", context, async () =>
        {
            var (ok, _, bytes, statusCode) = await Call(HttpMethod.Post, $"/prayers/{prayerId}/support-remove", token);
            return ok ? Response.Ok(statusCode: statusCode, sizeBytes: bytes) : Response.Fail(statusCode: statusCode.ToString());
        });

        await Step.Run("10_delete_prayer", context, async () =>
        {
            var (ok, _, bytes, statusCode) = await Call(HttpMethod.Delete, $"/prayers/{prayerId}", token);
            return ok ? Response.Ok(statusCode: statusCode, sizeBytes: bytes) : Response.Fail(statusCode: statusCode.ToString());
        });
    }
    else
    {
        var prayerSkipReason = s05.IsError
            ? "s05.IsError = true – prayer creation failed"
            : "prayerId is empty";
        skipTracker.Record("write_heavy", "06_update_prayer",                 prayerSkipReason);
        skipTracker.Record("write_heavy", "07_add_comment",                   prayerSkipReason);
        skipTracker.Record("write_heavy", "07b_get_comments_of_prayer",       prayerSkipReason);
        skipTracker.Record("write_heavy", "07c_update_comment",               prayerSkipReason);
        skipTracker.Record("write_heavy", "07d_add_sub_comment",              prayerSkipReason);
        skipTracker.Record("write_heavy", "07e_get_sub_comments",             prayerSkipReason);
        skipTracker.Record("write_heavy", "07f_delete_sub_comment",           prayerSkipReason);
        skipTracker.Record("write_heavy", "07g_add_reaction_to_comment",      prayerSkipReason);
        skipTracker.Record("write_heavy", "07h_remove_reaction_from_comment", prayerSkipReason);
        skipTracker.Record("write_heavy", "07i_delete_comment",               prayerSkipReason);
        skipTracker.Record("write_heavy", "08_support",                       prayerSkipReason);
        skipTracker.Record("write_heavy", "09_support_remove",                prayerSkipReason);
        skipTracker.Record("write_heavy", "10_delete_prayer",                 prayerSkipReason);
    }

    if (!string.IsNullOrEmpty(categoryId))
    {
        await Step.Run("11_delete_category", context, async () =>
        {
            var (ok, _, bytes, statusCode) = await Call(HttpMethod.Delete, $"/categories/{categoryId}", token);
            return ok ? Response.Ok(statusCode: statusCode, sizeBytes: bytes) : Response.Fail(statusCode: statusCode.ToString());
        });
    }
    else
    {
        skipTracker.Record("write_heavy", "11_delete_category", "categoryId is empty");
    }

    return Response.Ok();
});

// ===========================================================================
// CEN�RIO 2 � LEITURA INTENSIVA (read-heavy)
// Setup m�nimo e, em seguida, somente leituras (tr�fego dominante).
// Endpoints: GET /prayers/{id}, /prayers/timeline, /prayers/search (texto e autor),
//            GET /categories/{id}, /categories/user/{username}, /categories/mine,
//            GET /profile/{username}
// ===========================================================================
var readHeavy = Scenario.Create("read_heavy", async context =>
{
    var f = new Faker("pt_BR");
    string token = string.Empty, userName = string.Empty, categoryId = string.Empty, prayerId = string.Empty;

    var s01 = await Step.Run("01_setup_register", context, async () =>
    {
        string regStatus; int regBytes;
        (userName, token, regBytes, regStatus) = await Register(f);
        return string.IsNullOrEmpty(token)
            ? Response.Fail(statusCode: regStatus)
            : Response.Ok(statusCode: regStatus, sizeBytes: regBytes);
    });
    if (s01.IsError)
    {
        skipTracker.Record("read_heavy", "02_setup_category",    "s01.IsError = true – registration failed");
        skipTracker.Record("read_heavy", "03_setup_prayer",      "s01.IsError = true – registration failed");
        skipTracker.Record("read_heavy", "04_get_prayer_by_id",  "s01.IsError = true – registration failed");
        skipTracker.Record("read_heavy", "05_timeline",          "s01.IsError = true – registration failed");
        skipTracker.Record("read_heavy", "06_search_by_text",    "s01.IsError = true – registration failed");
        skipTracker.Record("read_heavy", "07_search_by_author",  "s01.IsError = true – registration failed");
        skipTracker.Record("read_heavy", "08_get_category_by_id","s01.IsError = true – registration failed");
        skipTracker.Record("read_heavy", "09_categories_by_username", "s01.IsError = true – registration failed");
        skipTracker.Record("read_heavy", "10_my_categories",     "s01.IsError = true – registration failed");
        skipTracker.Record("read_heavy", "11_get_profile",       "s01.IsError = true – registration failed");
        return Response.Fail();
    }

    await Step.Run("02_setup_category", context, async () =>
    {
        var (ok, body, bytes, statusCode) = await Call(HttpMethod.Post, "/categories/new", token,
            new { name = f.Commerce.Department(), description = f.Lorem.Sentence(), colorHex = $"#{f.Random.Hexadecimal(6, "")}", isPublic = true });
        if (ok) categoryId = ReadProp(body, "id") ?? string.Empty;
        return ok ? Response.Ok(statusCode: statusCode, sizeBytes: bytes) : Response.Fail(statusCode: statusCode.ToString());
    });

    await Step.Run("03_setup_prayer", context, async () =>
    {
        var categoryIds = string.IsNullOrEmpty(categoryId) ? Array.Empty<string>() : new[] { categoryId };
        var (ok, body, bytes, statusCode) = await Call(HttpMethod.Post, "/prayers/new", token,
            new { content = f.Lorem.Paragraph(), isPublic = true, categoryIds });
        if (ok) prayerId = ReadProp(body, "id") ?? string.Empty;
        return ok ? Response.Ok(statusCode: statusCode, sizeBytes: bytes) : Response.Fail(statusCode: statusCode.ToString());
    });

    if (!string.IsNullOrEmpty(prayerId))
    {
        await Step.Run("04_get_prayer_by_id", context, async () =>
        {
            var (ok, _, bytes, statusCode) = await Call(HttpMethod.Get, $"/prayers/{prayerId}");
            return ok ? Response.Ok(statusCode: statusCode, sizeBytes: bytes) : Response.Fail(statusCode: statusCode.ToString());
        });
    }
    else
    {
        skipTracker.Record("read_heavy", "04_get_prayer_by_id", "prayerId is empty");
    }

    await Step.Run("05_timeline", context, async () =>
    {
        var (ok, _, bytes, statusCode) = await Call(HttpMethod.Get, "/prayers/timeline?page=1&pageSize=10", token);
        return ok ? Response.Ok(statusCode: statusCode, sizeBytes: bytes) : Response.Fail(statusCode: statusCode.ToString());
    });

    await Step.Run("06_search_by_text", context, async () =>
    {
        var (ok, _, bytes, statusCode) = await Call(HttpMethod.Get, $"/prayers/search?Pagination.Page=1&Pagination.PageSize=10&Text={f.Lorem.Word()}");
        return ok ? Response.Ok(statusCode: statusCode, sizeBytes: bytes) : Response.Fail(statusCode: statusCode.ToString());
    });

    await Step.Run("07_search_by_author", context, async () =>
    {
        var (ok, _, bytes, statusCode) = await Call(HttpMethod.Get, $"/prayers/search?Pagination.Page=1&Pagination.PageSize=10&AuthorUserName={userName}");
        return ok ? Response.Ok(statusCode: statusCode, sizeBytes: bytes) : Response.Fail(statusCode: statusCode.ToString());
    });

    if (!string.IsNullOrEmpty(categoryId))
    {
        await Step.Run("08_get_category_by_id", context, async () =>
        {
            var (ok, _, bytes, statusCode) = await Call(HttpMethod.Get, $"/categories/{categoryId}");
            return ok ? Response.Ok(statusCode: statusCode, sizeBytes: bytes) : Response.Fail(statusCode: statusCode.ToString());
        });
    }
    else
    {
        skipTracker.Record("read_heavy", "08_get_category_by_id", "categoryId is empty");
    }

    await Step.Run("09_categories_by_username", context, async () =>
    {
        var (ok, _, bytes, statusCode) = await Call(HttpMethod.Get, $"/categories/user/{userName}");
        return ok ? Response.Ok(statusCode: statusCode, sizeBytes: bytes) : Response.Fail(statusCode: statusCode.ToString());
    });

    await Step.Run("10_my_categories", context, async () =>
    {
        var (ok, _, bytes, statusCode) = await Call(HttpMethod.Get, "/categories/mine", token);
        return ok ? Response.Ok(statusCode: statusCode, sizeBytes: bytes) : Response.Fail(statusCode: statusCode.ToString());
    });

    await Step.Run("11_get_profile", context, async () =>
    {
        var (ok, _, bytes, statusCode) = await Call(HttpMethod.Get, $"/profile/{userName}", token);
        return ok ? Response.Ok(statusCode: statusCode, sizeBytes: bytes) : Response.Fail(statusCode: statusCode.ToString());
    });

    return Response.Ok();
});

// ===========================================================================
// CEN�RIO 3 � GRAFO SOCIAL (graph-heavy / Neo4j)
// Ciclo completo de amizade entre dois usu�rios.
// Endpoints: /friendships/request, /friendships/requests/to-me,
//            /friendships/{id}/accept, /friendships/my-mutual-friends-with/{id},
//            /friendships/suggestions, GET /friendships,
//            /friendships/{id}/remove, /friendships/{id}/cancel-request,
//            /friendships/{id}/decline
// ===========================================================================
var graphHeavy = Scenario.Create("graph_heavy", async context =>
{
    var f = new Faker("pt_BR");
    string tokenA = string.Empty, tokenB = string.Empty, userIdB = string.Empty, reqId = string.Empty;

    var s01 = await Step.Run("01_register_a", context, async () =>
    {
        string regStatus; int regBytes;
        (_, tokenA, regBytes, regStatus) = await Register(f);
        return string.IsNullOrEmpty(tokenA)
            ? Response.Fail(statusCode: regStatus)
            : Response.Ok(statusCode: regStatus, sizeBytes: regBytes);
    });
    if (s01.IsError)
    {
        skipTracker.Record("graph_heavy", "02_register_b",       "s01.IsError = true – user A registration failed");
        skipTracker.Record("graph_heavy", "03_get_profile_b",    "s01.IsError = true – user A registration failed");
        skipTracker.Record("graph_heavy", "04_send_request",     "s01.IsError = true – user A registration failed");
        skipTracker.Record("graph_heavy", "05_requests_to_me_b", "s01.IsError = true – user A registration failed");
        skipTracker.Record("graph_heavy", "06_accept_request",   "s01.IsError = true – user A registration failed");
        skipTracker.Record("graph_heavy", "07_mutual_friends",   "s01.IsError = true – user A registration failed");
        skipTracker.Record("graph_heavy", "08_suggestions",      "s01.IsError = true – user A registration failed");
        skipTracker.Record("graph_heavy", "09_my_friendships",   "s01.IsError = true – user A registration failed");
        skipTracker.Record("graph_heavy", "10_remove_friend",    "s01.IsError = true – user A registration failed");
        skipTracker.Record("graph_heavy", "11_send_to_cancel",   "s01.IsError = true – user A registration failed");
        skipTracker.Record("graph_heavy", "12_cancel_request",   "s01.IsError = true – user A registration failed");
        skipTracker.Record("graph_heavy", "13_send_to_decline",  "s01.IsError = true – user A registration failed");
        skipTracker.Record("graph_heavy", "14_decline_request",  "s01.IsError = true – user A registration failed");
        return Response.Fail();
    }

    string userNameB = string.Empty;
    var s02 = await Step.Run("02_register_b", context, async () =>
    {
        string regStatus; int regBytes;
        (userNameB, tokenB, regBytes, regStatus) = await Register(f);
        return string.IsNullOrEmpty(tokenB)
            ? Response.Fail(statusCode: regStatus)
            : Response.Ok(statusCode: regStatus, sizeBytes: regBytes);
    });
    if (s02.IsError)
    {
        skipTracker.Record("graph_heavy", "03_get_profile_b",    "s02.IsError = true – user B registration failed");
        skipTracker.Record("graph_heavy", "04_send_request",     "s02.IsError = true – user B registration failed");
        skipTracker.Record("graph_heavy", "05_requests_to_me_b", "s02.IsError = true – user B registration failed");
        skipTracker.Record("graph_heavy", "06_accept_request",   "s02.IsError = true – user B registration failed");
        skipTracker.Record("graph_heavy", "07_mutual_friends",   "s02.IsError = true – user B registration failed");
        skipTracker.Record("graph_heavy", "08_suggestions",      "s02.IsError = true – user B registration failed");
        skipTracker.Record("graph_heavy", "09_my_friendships",   "s02.IsError = true – user B registration failed");
        skipTracker.Record("graph_heavy", "10_remove_friend",    "s02.IsError = true – user B registration failed");
        skipTracker.Record("graph_heavy", "11_send_to_cancel",   "s02.IsError = true – user B registration failed");
        skipTracker.Record("graph_heavy", "12_cancel_request",   "s02.IsError = true – user B registration failed");
        skipTracker.Record("graph_heavy", "13_send_to_decline",  "s02.IsError = true – user B registration failed");
        skipTracker.Record("graph_heavy", "14_decline_request",  "s02.IsError = true – user B registration failed");
        return Response.Fail();
    }

    var s03 = await Step.Run("03_get_profile_b", context, async () =>
    {
        var (ok, body, bytes, statusCode) = await Call(HttpMethod.Get, $"/profile/{userNameB}", tokenA);
        if (ok) userIdB = ReadProp(body, "id") ?? string.Empty;
        return ok ? Response.Ok(statusCode: statusCode, sizeBytes: bytes) : Response.Fail(statusCode: statusCode.ToString());
    });
    if (s03.IsError || string.IsNullOrEmpty(userIdB))
    {
        var s03SkipReason = s03.IsError
            ? "s03.IsError = true – profile B not found"
            : "userIdB is empty";
        skipTracker.Record("graph_heavy", "04_send_request",     s03SkipReason);
        skipTracker.Record("graph_heavy", "05_requests_to_me_b", s03SkipReason);
        skipTracker.Record("graph_heavy", "06_accept_request",   s03SkipReason);
        skipTracker.Record("graph_heavy", "07_mutual_friends",   s03SkipReason);
        skipTracker.Record("graph_heavy", "08_suggestions",      s03SkipReason);
        skipTracker.Record("graph_heavy", "09_my_friendships",   s03SkipReason);
        skipTracker.Record("graph_heavy", "10_remove_friend",    s03SkipReason);
        skipTracker.Record("graph_heavy", "11_send_to_cancel",   s03SkipReason);
        skipTracker.Record("graph_heavy", "12_cancel_request",   s03SkipReason);
        skipTracker.Record("graph_heavy", "13_send_to_decline",  s03SkipReason);
        skipTracker.Record("graph_heavy", "14_decline_request",  s03SkipReason);
        return Response.Ok();
    }

    var s04 = await Step.Run("04_send_request", context, async () =>
    {
        var (ok, body, bytes, statusCode) = await Call(HttpMethod.Post, "/friendships/request", tokenA, new { toUserId = userIdB });
        if (ok) reqId = ReadProp(body, "id") ?? string.Empty;
        return ok ? Response.Ok(statusCode: statusCode, sizeBytes: bytes) : Response.Fail(statusCode: statusCode.ToString());
    });

    await Step.Run("05_requests_to_me_b", context, async () =>
    {
        var (ok, _, bytes, statusCode) = await Call(HttpMethod.Get, "/friendships/requests/to-me?page=1&pageSize=10", tokenB);
        return ok ? Response.Ok(statusCode: statusCode, sizeBytes: bytes) : Response.Fail(statusCode: statusCode.ToString());
    });

    if (!s04.IsError && !string.IsNullOrEmpty(reqId))
    {
        await Step.Run("06_accept_request", context, async () =>
        {
            var (ok, _, bytes, statusCode) = await Call(HttpMethod.Post, $"/friendships/{reqId}/accept", tokenB);
            return ok ? Response.Ok(statusCode: statusCode, sizeBytes: bytes) : Response.Fail(statusCode: statusCode.ToString());
        });

        await Step.Run("07_mutual_friends", context, async () =>
        {
            var (ok, _, bytes, statusCode) = await Call(HttpMethod.Get, $"/friendships/my-mutual-friends-with/{userIdB}?page=1&pageSize=10", tokenA);
            return ok ? Response.Ok(statusCode: statusCode, sizeBytes: bytes) : Response.Fail(statusCode: statusCode.ToString());
        });

        await Step.Run("08_suggestions", context, async () =>
        {
            var (ok, _, bytes, statusCode) = await Call(HttpMethod.Get, "/friendships/suggestions?page=1&pageSize=5", tokenA);
            return ok ? Response.Ok(statusCode: statusCode, sizeBytes: bytes) : Response.Fail(statusCode: statusCode.ToString());
        });

        await Step.Run("09_my_friendships", context, async () =>
        {
            var (ok, _, bytes, statusCode) = await Call(HttpMethod.Get, "/friendships?page=1&pageSize=10", tokenA);
            return ok ? Response.Ok(statusCode: statusCode, sizeBytes: bytes) : Response.Fail(statusCode: statusCode.ToString());
        });

        await Step.Run("10_remove_friend", context, async () =>
        {
            var (ok, _, bytes, statusCode) = await Call(HttpMethod.Post, $"/friendships/{userIdB}/remove", tokenA);
            return ok ? Response.Ok(statusCode: statusCode, sizeBytes: bytes) : Response.Fail(statusCode: statusCode.ToString());
        });
    }
    else
    {
        var s04SkipReason = s04.IsError
            ? "s04.IsError = true – friend request failed"
            : "reqId is empty";
        skipTracker.Record("graph_heavy", "06_accept_request", s04SkipReason);
        skipTracker.Record("graph_heavy", "07_mutual_friends", s04SkipReason);
        skipTracker.Record("graph_heavy", "08_suggestions",    s04SkipReason);
        skipTracker.Record("graph_heavy", "09_my_friendships", s04SkipReason);
        skipTracker.Record("graph_heavy", "10_remove_friend",  s04SkipReason);
    }

    // Pedido + cancelamento (pelo solicitante).
    string cancelId = string.Empty;
    var s11 = await Step.Run("11_send_to_cancel", context, async () =>
    {
        var (ok, body, bytes, statusCode) = await Call(HttpMethod.Post, "/friendships/request", tokenA, new { toUserId = userIdB });
        if (ok) cancelId = ReadProp(body, "id") ?? string.Empty;
        return ok ? Response.Ok(statusCode: statusCode, sizeBytes: bytes) : Response.Fail(statusCode: statusCode.ToString());
    });
    if (!s11.IsError && !string.IsNullOrEmpty(cancelId))
    {
        await Step.Run("12_cancel_request", context, async () =>
        {
            var (ok, _, bytes, statusCode) = await Call(HttpMethod.Post, $"/friendships/{cancelId}/cancel-request", tokenA);
            return ok ? Response.Ok(statusCode: statusCode, sizeBytes: bytes) : Response.Fail(statusCode: statusCode.ToString());
        });
    }
    else
    {
        var s11SkipReason = s11.IsError
            ? "s11.IsError = true – send-to-cancel failed"
            : "cancelId is empty";
        skipTracker.Record("graph_heavy", "12_cancel_request", s11SkipReason);
    }

    // Pedido + recusa (pelo destinat�rio).
    string declineId = string.Empty;
    var s13 = await Step.Run("13_send_to_decline", context, async () =>
    {
        var (ok, body, bytes, statusCode) = await Call(HttpMethod.Post, "/friendships/request", tokenA, new { toUserId = userIdB });
        if (ok) declineId = ReadProp(body, "id") ?? string.Empty;
        return ok ? Response.Ok(statusCode: statusCode, sizeBytes: bytes) : Response.Fail(statusCode: statusCode.ToString());
    });
    if (!s13.IsError && !string.IsNullOrEmpty(declineId))
    {
        await Step.Run("14_decline_request", context, async () =>
        {
            var (ok, _, bytes, statusCode) = await Call(HttpMethod.Post, $"/friendships/{declineId}/decline", tokenB);
            return ok ? Response.Ok(statusCode: statusCode, sizeBytes: bytes) : Response.Fail(statusCode: statusCode.ToString());
        });
    }
    else
    {
        var s13SkipReason = s13.IsError
            ? "s13.IsError = true – send-to-decline failed"
            : "declineId is empty";
        skipTracker.Record("graph_heavy", "14_decline_request", s13SkipReason);
    }

    return Response.Ok();
});

// ===========================================================================
// CEN�RIO 4 � DOCUMENTOS (document-heavy / MongoDB)
// Foco nas entidades documentais (categorias e ora��es): escrita + leitura
// concentradas no store de documentos.
// Endpoints: /categories/new, /prayers/new, GET /prayers/{id}, PUT /prayers/{id},
//            /prayers/search, /prayers/timeline, GET /categories/{id}
// ===========================================================================
var documentHeavy = Scenario.Create("document_heavy", async context =>
{
    var f = new Faker("pt_BR");
    string token = string.Empty, categoryId = string.Empty, prayerId = string.Empty;

    var s01 = await Step.Run("01_register", context, async () =>
    {
        string regStatus; int regBytes;
        (_, token, regBytes, regStatus) = await Register(f);
        return string.IsNullOrEmpty(token)
            ? Response.Fail(statusCode: regStatus)
            : Response.Ok(statusCode: regStatus, sizeBytes: regBytes);
    });
    if (s01.IsError)
    {
        skipTracker.Record("document_heavy", "02_create_category", "s01.IsError = true – registration failed");
        skipTracker.Record("document_heavy", "03_create_prayer",   "s01.IsError = true – registration failed");
        skipTracker.Record("document_heavy", "04_get_prayer",      "s01.IsError = true – registration failed");
        skipTracker.Record("document_heavy", "05_update_prayer",   "s01.IsError = true – registration failed");
        skipTracker.Record("document_heavy", "06_search",          "s01.IsError = true – registration failed");
        skipTracker.Record("document_heavy", "07_timeline",        "s01.IsError = true – registration failed");
        skipTracker.Record("document_heavy", "08_get_category",    "s01.IsError = true – registration failed");
        return Response.Fail();
    }

    await Step.Run("02_create_category", context, async () =>
    {
        var (ok, body, bytes, statusCode) = await Call(HttpMethod.Post, "/categories/new", token,
            new { name = f.Commerce.Department(), description = f.Lorem.Sentence(), colorHex = $"#{f.Random.Hexadecimal(6, "")}", isPublic = true });
        if (ok) categoryId = ReadProp(body, "id") ?? string.Empty;
        return ok ? Response.Ok(statusCode: statusCode, sizeBytes: bytes) : Response.Fail(statusCode: statusCode.ToString());
    });

    var s03 = await Step.Run("03_create_prayer", context, async () =>
    {
        var categoryIds = string.IsNullOrEmpty(categoryId) ? Array.Empty<string>() : new[] { categoryId };
        var (ok, body, bytes, statusCode) = await Call(HttpMethod.Post, "/prayers/new", token,
            new { content = f.Lorem.Paragraphs(2), isPublic = true, categoryIds });
        if (ok) prayerId = ReadProp(body, "id") ?? string.Empty;
        return ok ? Response.Ok(statusCode: statusCode, sizeBytes: bytes) : Response.Fail(statusCode: statusCode.ToString());
    });

    if (!s03.IsError && !string.IsNullOrEmpty(prayerId))
    {
        await Step.Run("04_get_prayer", context, async () =>
        {
            var (ok, _, bytes, statusCode) = await Call(HttpMethod.Get, $"/prayers/{prayerId}");
            return ok ? Response.Ok(statusCode: statusCode, sizeBytes: bytes) : Response.Fail(statusCode: statusCode.ToString());
        });

        await Step.Run("05_update_prayer", context, async () =>
        {
            var (ok, _, bytes, statusCode) = await Call(HttpMethod.Put, $"/prayers/{prayerId}", token,
                new { content = f.Lorem.Paragraph(), isPublic = true, categoryIds = Array.Empty<string>() });
            return ok ? Response.Ok(statusCode: statusCode, sizeBytes: bytes) : Response.Fail(statusCode: statusCode.ToString());
        });
    }
    else
    {
        var s03SkipReason = s03.IsError
            ? "s03.IsError = true – prayer creation failed"
            : "prayerId is empty";
        skipTracker.Record("document_heavy", "04_get_prayer",    s03SkipReason);
        skipTracker.Record("document_heavy", "05_update_prayer", s03SkipReason);
    }

    await Step.Run("06_search", context, async () =>
    {
        var (ok, _, bytes, statusCode) = await Call(HttpMethod.Get, $"/prayers/search?Pagination.Page=1&Pagination.PageSize=20&Text={f.Lorem.Word()}");
        return ok ? Response.Ok(statusCode: statusCode, sizeBytes: bytes) : Response.Fail(statusCode: statusCode.ToString());
    });

    await Step.Run("07_timeline", context, async () =>
    {
        var (ok, _, bytes, statusCode) = await Call(HttpMethod.Get, "/prayers/timeline?page=1&pageSize=20", token);
        return ok ? Response.Ok(statusCode: statusCode, sizeBytes: bytes) : Response.Fail(statusCode: statusCode.ToString());
    });

    if (!string.IsNullOrEmpty(categoryId))
    {
        await Step.Run("08_get_category", context, async () =>
        {
            var (ok, _, bytes, statusCode) = await Call(HttpMethod.Get, $"/categories/{categoryId}");
            return ok ? Response.Ok(statusCode: statusCode, sizeBytes: bytes) : Response.Fail(statusCode: statusCode.ToString());
        });
    }
    else
    {
        skipTracker.Record("document_heavy", "08_get_category", "categoryId is empty");
    }

    return Response.Ok();
});

// ===========================================================================
// CEN�RIO 5 � MISTA: GRAFO + DOCUMENTOS (cross-store)
// Jornada que cruza MongoDB (documentos) e Neo4j (grafo), incluindo login e
// retrospectiva (agrega��o).
// Endpoints: /auth/login, /prayers/new, /friendships/request,
//            /friendships/{id}/accept, /prayers/timeline, /prayers/search,
//            /friendships/my-mutual-friends-with/{id}, /prayers/{id}/comment,
//            /retrospective/my-retrospective
// ===========================================================================
var mixed = Scenario.Create("mixed_graph_documents", async context =>
{
    var f = new Faker("pt_BR");
    string userNameA = string.Empty, tokenA = string.Empty, tokenB = string.Empty;
    string userNameB = string.Empty, userIdB = string.Empty, prayerId = string.Empty, reqId = string.Empty;

    var s01 = await Step.Run("01_register_a", context, async () =>
    {
        string regStatus; int regBytes;
        (userNameA, tokenA, regBytes, regStatus) = await Register(f);
        return string.IsNullOrEmpty(tokenA)
            ? Response.Fail(statusCode: regStatus)
            : Response.Ok(statusCode: regStatus, sizeBytes: regBytes);
    });
    if (s01.IsError)
    {
        skipTracker.Record("mixed_graph_documents", "02_login_a",        "s01.IsError = true – user A registration failed");
        skipTracker.Record("mixed_graph_documents", "03_register_b",     "s01.IsError = true – user A registration failed");
        skipTracker.Record("mixed_graph_documents", "04_get_profile_b",  "s01.IsError = true – user A registration failed");
        skipTracker.Record("mixed_graph_documents", "05_create_prayer",  "s01.IsError = true – user A registration failed");
        skipTracker.Record("mixed_graph_documents", "06_send_request",   "s01.IsError = true – user A registration failed");
        skipTracker.Record("mixed_graph_documents", "07_accept_request", "s01.IsError = true – user A registration failed");
        skipTracker.Record("mixed_graph_documents", "08_timeline",       "s01.IsError = true – user A registration failed");
        skipTracker.Record("mixed_graph_documents", "09_mutual_friends", "s01.IsError = true – user A registration failed");
        skipTracker.Record("mixed_graph_documents", "10_search",         "s01.IsError = true – user A registration failed");
        skipTracker.Record("mixed_graph_documents", "11_add_comment",    "s01.IsError = true – user A registration failed");
        skipTracker.Record("mixed_graph_documents", "12_retrospective",  "s01.IsError = true – user A registration failed");
        return Response.Fail();
    }

    await Step.Run("02_login_a", context, async () =>
    {
        var (ok, body, bytes, statusCode) = await Call(HttpMethod.Post, "/auth/login", payload: new { userName = userNameA, password = "Teste@123" });
        if (ok)
        {
            var t = ReadProp(body, "token");
            if (!string.IsNullOrEmpty(t)) tokenA = t;
        }
        return ok ? Response.Ok(statusCode: statusCode, sizeBytes: bytes) : Response.Fail(statusCode: statusCode.ToString());
    });

    var s03 = await Step.Run("03_register_b", context, async () =>
    {
        string regStatus; int regBytes;
        (userNameB, tokenB, regBytes, regStatus) = await Register(f);
        return string.IsNullOrEmpty(tokenB)
            ? Response.Fail(statusCode: regStatus)
            : Response.Ok(statusCode: regStatus, sizeBytes: regBytes);
    });
    if (s03.IsError)
    {
        skipTracker.Record("mixed_graph_documents", "04_get_profile_b",  "s03.IsError = true – user B registration failed");
        skipTracker.Record("mixed_graph_documents", "05_create_prayer",  "s03.IsError = true – user B registration failed");
        skipTracker.Record("mixed_graph_documents", "06_send_request",   "s03.IsError = true – user B registration failed");
        skipTracker.Record("mixed_graph_documents", "07_accept_request", "s03.IsError = true – user B registration failed");
        skipTracker.Record("mixed_graph_documents", "08_timeline",       "s03.IsError = true – user B registration failed");
        skipTracker.Record("mixed_graph_documents", "09_mutual_friends", "s03.IsError = true – user B registration failed");
        skipTracker.Record("mixed_graph_documents", "10_search",         "s03.IsError = true – user B registration failed");
        skipTracker.Record("mixed_graph_documents", "11_add_comment",    "s03.IsError = true – user B registration failed");
        skipTracker.Record("mixed_graph_documents", "12_retrospective",  "s03.IsError = true – user B registration failed");
        return Response.Fail();
    }

    await Step.Run("04_get_profile_b", context, async () =>
    {
        var (ok, body, bytes, statusCode) = await Call(HttpMethod.Get, $"/profile/{userNameB}", tokenA);
        if (ok) userIdB = ReadProp(body, "id") ?? string.Empty;
        return ok ? Response.Ok(statusCode: statusCode, sizeBytes: bytes) : Response.Fail(statusCode: statusCode.ToString());
    });

    var s05 = await Step.Run("05_create_prayer", context, async () =>
    {
        var (ok, body, bytes, statusCode) = await Call(HttpMethod.Post, "/prayers/new", tokenA,
            new { content = f.Lorem.Paragraph(), isPublic = true, categoryIds = Array.Empty<string>() });
        if (ok) prayerId = ReadProp(body, "id") ?? string.Empty;
        return ok ? Response.Ok(statusCode: statusCode, sizeBytes: bytes) : Response.Fail(statusCode: statusCode.ToString());
    });

    if (!string.IsNullOrEmpty(userIdB))
    {
        var s06 = await Step.Run("06_send_request", context, async () =>
        {
            var (ok, body, bytes, statusCode) = await Call(HttpMethod.Post, "/friendships/request", tokenA, new { toUserId = userIdB });
            if (ok) reqId = ReadProp(body, "id") ?? string.Empty;
            return ok ? Response.Ok(statusCode: statusCode, sizeBytes: bytes) : Response.Fail(statusCode: statusCode.ToString());
        });

        if (!s06.IsError && !string.IsNullOrEmpty(reqId))
        {
            await Step.Run("07_accept_request", context, async () =>
            {
                var (ok, _, bytes, statusCode) = await Call(HttpMethod.Post, $"/friendships/{reqId}/accept", tokenB);
                return ok ? Response.Ok(statusCode: statusCode, sizeBytes: bytes) : Response.Fail(statusCode: statusCode.ToString());
            });

            await Step.Run("09_mutual_friends", context, async () =>
            {
                var (ok, _, bytes, statusCode) = await Call(HttpMethod.Get, $"/friendships/my-mutual-friends-with/{userIdB}?page=1&pageSize=10", tokenA);
                return ok ? Response.Ok(statusCode: statusCode, sizeBytes: bytes) : Response.Fail(statusCode: statusCode.ToString());
            });
        }
        else
        {
            var s06SkipReason = s06.IsError
                ? "s06.IsError = true – friend request failed"
                : "reqId is empty";
            skipTracker.Record("mixed_graph_documents", "07_accept_request", s06SkipReason);
            skipTracker.Record("mixed_graph_documents", "09_mutual_friends", s06SkipReason);
        }
    }
    else
    {
        skipTracker.Record("mixed_graph_documents", "06_send_request",   "userIdB is empty");
        skipTracker.Record("mixed_graph_documents", "07_accept_request", "userIdB is empty");
        skipTracker.Record("mixed_graph_documents", "09_mutual_friends", "userIdB is empty");
    }

    await Step.Run("08_timeline", context, async () =>
    {
        var (ok, _, bytes, statusCode) = await Call(HttpMethod.Get, "/prayers/timeline?page=1&pageSize=10", tokenA);
        return ok ? Response.Ok(statusCode: statusCode, sizeBytes: bytes) : Response.Fail(statusCode: statusCode.ToString());
    });

    await Step.Run("10_search", context, async () =>
    {
        var (ok, _, bytes, statusCode) = await Call(HttpMethod.Get, $"/prayers/search?Pagination.Page=1&Pagination.PageSize=10&Text={f.Lorem.Word()}");
        return ok ? Response.Ok(statusCode: statusCode, sizeBytes: bytes) : Response.Fail(statusCode: statusCode.ToString());
    });

    if (!s05.IsError && !string.IsNullOrEmpty(prayerId))
    {
        string mixedCommentId = string.Empty;
        var s11 = await Step.Run("11_add_comment", context, async () =>
        {
            var (ok, body, bytes, statusCode) = await Call(HttpMethod.Post, $"/comments/prayer/{prayerId}", tokenA,
                new { content = f.Lorem.Sentence() });
            if (ok) mixedCommentId = ReadProp(body, "comment.id") ?? string.Empty;
            return ok ? Response.Ok(statusCode: statusCode, sizeBytes: bytes) : Response.Fail(statusCode: statusCode.ToString());
        });

        if (!s11.IsError && !string.IsNullOrEmpty(mixedCommentId))
        {
            await Step.Run("11b_get_comments_of_prayer", context, async () =>
            {
                var (ok, _, bytes, statusCode) = await Call(HttpMethod.Get, $"/comments/prayer/{prayerId}?page=1&pageSize=10", tokenA);
                return ok ? Response.Ok(statusCode: statusCode, sizeBytes: bytes) : Response.Fail(statusCode: statusCode.ToString());
            });

            await Step.Run("11c_add_reaction_to_comment", context, async () =>
            {
                var (ok, _, bytes, statusCode) = await Call(HttpMethod.Post, $"/comments/{mixedCommentId}/support", tokenA);
                return ok ? Response.Ok(statusCode: statusCode, sizeBytes: bytes) : Response.Fail(statusCode: statusCode.ToString());
            });

            await Step.Run("11d_remove_reaction_from_comment", context, async () =>
            {
                var (ok, _, bytes, statusCode) = await Call(HttpMethod.Post, $"/comments/{mixedCommentId}/remove-support", tokenA);
                return ok ? Response.Ok(statusCode: statusCode, sizeBytes: bytes) : Response.Fail(statusCode: statusCode.ToString());
            });
        }
        else
        {
            var commentSkipReason = s11.IsError
                ? "s11.IsError = true – comment creation failed"
                : "mixedCommentId is empty";
            skipTracker.Record("mixed_graph_documents", "11b_get_comments_of_prayer",      commentSkipReason);
            skipTracker.Record("mixed_graph_documents", "11c_add_reaction_to_comment",     commentSkipReason);
            skipTracker.Record("mixed_graph_documents", "11d_remove_reaction_from_comment", commentSkipReason);
        }
    }
    else
    {
        var s05SkipReason = s05.IsError
            ? "s05.IsError = true – prayer creation failed"
            : "prayerId is empty";
        skipTracker.Record("mixed_graph_documents", "11_add_comment",                  s05SkipReason);
        skipTracker.Record("mixed_graph_documents", "11b_get_comments_of_prayer",      s05SkipReason);
        skipTracker.Record("mixed_graph_documents", "11c_add_reaction_to_comment",     s05SkipReason);
        skipTracker.Record("mixed_graph_documents", "11d_remove_reaction_from_comment", s05SkipReason);
    }

    await Step.Run("12_retrospective", context, async () =>
    {
        var from = DateTime.UtcNow.AddDays(-30).ToString("yyyy-MM-dd");
        var to = DateTime.UtcNow.ToString("yyyy-MM-dd");
        var (ok, _, bytes, statusCode) = await Call(HttpMethod.Get, $"/retrospective/my-retrospective?FromDate={from}&ToDate={to}", tokenA);
        return ok ? Response.Ok(statusCode: statusCode, sizeBytes: bytes) : Response.Fail(statusCode: statusCode.ToString());
    });

    return Response.Ok();
});

// ---------------------------------------------------------------------------
// Sele��o dos cen�rios conforme a configura��o.
// ---------------------------------------------------------------------------
// RequestsPerIteration = quantidade de Step.Run declarados no cen�rio, ou seja, quantos
// requests HTTP uma itera��o completa dispara. MANTER EM SINCRONIA ao adicionar ou
// remover steps: � o fator que converte o alvo de requests/s do n�vel de carga em
// itera��es/s para o Simulation.Inject. Se ficar defasado, o throughput te�rico
// reportado deixa de corresponder � carga realmente injetada.
var allScenarios = new Dictionary<string, (ScenarioProps Props, ScenarioShape Shape)>(StringComparer.OrdinalIgnoreCase)
{
    ["write"]    = (writeHeavy,    new ScenarioShape("write",    "write_heavy",           19)),
    ["read"]     = (readHeavy,     new ScenarioShape("read",     "read_heavy",            11)),
    ["graph"]    = (graphHeavy,    new ScenarioShape("graph",    "graph_heavy",           14)),
    ["document"] = (documentHeavy, new ScenarioShape("document", "document_heavy",         8)),
    ["mixed"]    = (mixed,         new ScenarioShape("mixed",    "mixed_graph_documents", 15))
};

(ScenarioProps Props, ScenarioShape Shape)[] selected;
if (config.Scenario.Equals("all", StringComparison.OrdinalIgnoreCase))
    selected = allScenarios.Values.ToArray();
else if (allScenarios.TryGetValue(config.Scenario, out var one))
    selected = [one];
else
    throw new ArgumentException(
        $"Cen�rio desconhecido: '{config.Scenario}'. Use: all | write | read | graph | document | mixed.");

// ---------------------------------------------------------------------------
// Plano de throughput: alvo do n�vel de carga -> itera��es/s por cen�rio.
// Calculado sobre os cen�rios REALMENTE selecionados, ent�o o alvo em requests/s vale
// tanto para "all" quanto para um cen�rio isolado.
// ---------------------------------------------------------------------------
var plan = ThroughputPlanner.Plan(profile, selected.Select(s => s.Shape).ToArray());
var ratePerScenario = plan.Scenarios.ToDictionary(s => s.ScenarioName, s => s.InjectionRatePerSecond);

Console.WriteLine();
Console.WriteLine($"Plano de carga (nivel={plan.LoadLevel}): alvo {plan.TargetRequestsPerSecond} req HTTP/s, " +
                  $"teorico {plan.TheoreticalRequestsPerSecond} req HTTP/s ({plan.DeviationFromTargetPercent:+0.0;-0.0;0}% do alvo), " +
                  $"{plan.IterationsPerSecond} iteracoes/s, {plan.TheoreticalTotalRequests} requests em {plan.DurationSeconds}s");
foreach (var s in plan.Scenarios)
    Console.WriteLine($"  {s.ScenarioName,-24} {s.InjectionRatePerSecond,3} it/s x {s.RequestsPerIteration,2} req/it = " +
                      $"{s.TheoreticalRequestsPerSecond,4} req HTTP/s teoricos");
Console.WriteLine();

var configured = selected.Select(s => Configure(s.Props, ratePerScenario[s.Shape.Name])).ToArray();

// ---------------------------------------------------------------------------
// Pasta de relat�rio �nica por execu��o (n�o sobrescreve execu��es anteriores).
// ---------------------------------------------------------------------------
var timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
var runName = $"{profile.Name}_{config.Scenario}_{timestamp}";
// Caminho absoluto para garantir que o CSV e o HTML do NBomber fiquem no mesmo lugar,
// independentemente do diretório de trabalho.
var reportFolder = Path.GetFullPath(Path.Combine(projectRoot, "reports", approach.Label, runName));

// ---------------------------------------------------------------------------
// Coleta opcional de CPU/mem�ria dos containers durante a execu��o.
// ---------------------------------------------------------------------------
ResourceSampler? sampler = null;
if (config.ResourceMonitoring.Enabled)
{
    try
    {
        sampler = new ResourceSampler(
            approach.Containers,
            config.ResourceMonitoring.SampleIntervalSeconds,
            Path.Combine(reportFolder, "resource-usage.csv"));
        sampler.Start();
        Console.WriteLine($"Coleta de recursos ativa: {string.Join(", ", approach.Containers)}");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Aviso: coleta de recursos desabilitada ({ex.Message}).");
        sampler = null;
    }
}

// ---------------------------------------------------------------------------
// Execução do NBomber. O relatório HTML (latência/throughput) é o artefato principal.
// ---------------------------------------------------------------------------
int exitCode = 0;
try
{
    NodeStats stats = NBomberRunner
        .RegisterScenarios(configured)
        .WithReportFolder(reportFolder)
        .WithReportFormats(ReportFormat.Html, ReportFormat.Csv)
        .Run();

    // Relatório de erros por endpoint (Req 2, 5, 6). Nunca interrompe o fluxo.
    var failures = ErrorReportExtensions.ExtractFailures(stats).ToList();
    ErrorReportWriter.TryWrite(reportFolder, failures);

    // Relatório de steps pulados (Req 8.1, 8.7).
    var skipped = skipTracker.Snapshot();
    SkippedStepsWriter.TryWrite(reportFolder, skipped);

    // Relatório de throughput: teórico planejado vs. real entregue pela API.
    var requestCounts = ThroughputReportWriter.ExtractRequestCounts(stats);
    ThroughputReportWriter.TryWrite(reportFolder, plan, requestCounts);

    // -----------------------------------------------------------------------
    // Resumo de falhas no console — notificação imediata ao operador.
    // -----------------------------------------------------------------------
    Console.WriteLine();
    Console.WriteLine("══════════════════════════════════════════════════════");
    Console.WriteLine("  RESUMO DA EXECUÇÃO");
    Console.WriteLine("══════════════════════════════════════════════════════");

    // -----------------------------------------------------------------------
    // Throughput: quanto foi pedido (teórico) vs. quanto a API entregou (real).
    // Aparece antes dos erros porque explica a maior parte deles quando há saturação.
    // -----------------------------------------------------------------------
    var measuredRps = ThroughputReportWriter.RequestsPerSecond(
        requestCounts.Values.Sum(), plan.DurationSeconds);
    var attainment = plan.TheoreticalRequestsPerSecond > 0
        ? measuredRps / plan.TheoreticalRequestsPerSecond * 100.0
        : 0;

    Console.WriteLine($"  Throughput HTTP  alvo={plan.TargetRequestsPerSecond} req/s  " +
                      $"teorico={plan.TheoreticalRequestsPerSecond} req/s  " +
                      $"real={measuredRps:F1} req/s  ({attainment:F0}% do teorico)");
    foreach (var s in plan.Scenarios)
    {
        requestCounts.TryGetValue(s.ScenarioName, out var count);
        var rps = ThroughputReportWriter.RequestsPerSecond(count, plan.DurationSeconds);
        Console.WriteLine($"    {s.ScenarioName,-24} teorico={s.TheoreticalRequestsPerSecond,4} req/s  real={rps,7:F1} req/s");
    }
    Console.WriteLine($"    -> detalhes em {ThroughputReportWriter.FileName}");
    Console.WriteLine();

    // Falhas HTTP por endpoint (contabilizadas pelo NBomber).
    if (failures.Count > 0)
    {
        exitCode = 1;
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"  ✗ ERROS HTTP: {failures.Sum(f => f.Count)} ocorrência(s) em {failures.Count} endpoint(s)");
        Console.ResetColor();
        foreach (var f in ErrorReportBuilder.Build(failures))
            Console.WriteLine($"    [{f.Scenario}] {f.Endpoint} → {f.ErrorMessage} (x{f.Count})");
    }
    else
    {
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("  ✓ Nenhum erro HTTP registrado.");
        Console.ResetColor();
    }

    // Steps pulados por dependência de dados (contabilizados pelo SkipTracker).
    if (skipped.Count > 0)
    {
        exitCode = 1;
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"  ⚠ STEPS PULADOS: {skipped.Count} step(s) não executado(s) por falha em step anterior");
        Console.ResetColor();
        foreach (var s in skipped)
            Console.WriteLine($"    [{s.Scenario}] {s.StepName} → {s.Reason}");
    }
    else
    {
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("  ✓ Nenhum step foi pulado.");
        Console.ResetColor();
    }

    // Estatísticas de cenários do NBomber (falhas percentuais por cenário).
    Console.WriteLine();
    Console.WriteLine("  Estatísticas por cenário:");
    foreach (var scenario in stats.ScenarioStats)
    {
        var allOk  = scenario.Ok.Request.Count;
        var allFail = scenario.Fail.Request.Count;
        var total   = allOk + allFail;
        var failPct = total > 0 ? (double)allFail / total * 100.0 : 0.0;

        var color = failPct > 5.0 ? ConsoleColor.Red
                  : failPct > 0.0 ? ConsoleColor.Yellow
                  : ConsoleColor.Green;

        Console.ForegroundColor = color;
        Console.WriteLine($"    {scenario.ScenarioName,-30} ok={allOk,6}  fail={allFail,6}  fail%={failPct:F1}%");
        Console.ResetColor();

        if (failPct > 5.0) exitCode = 1;
    }

    Console.WriteLine("══════════════════════════════════════════════════════");
    Console.WriteLine($"  Relatório HTML: {Path.GetFullPath(reportFolder)}");
    if (exitCode != 0)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine("  *** EXECUÇÃO CONCLUÍDA COM FALHAS — verifique os relatórios acima ***");
        Console.ResetColor();
    }
    Console.WriteLine("══════════════════════════════════════════════════════");
}
finally
{
    if (sampler is not null)
        await sampler.DisposeAsync();
    http.Dispose();
}

Environment.Exit(exitCode);
