// ========================================================================
// Digital Co-Founder BPO Agent v23.0 – PHASE 3 (Workers, API, Program)
// ========================================================================
// FINAL – All background workers and API endpoints.
// ========================================================================

using System;
using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using Prometheus;
using PuppeteerSharp;
using HtmlAgilityPack;
using Polly;
using Polly.Retry;
using Serilog;
using FluentValidation;
using FluentValidation.AspNetCore;
using FluentEmail.Core;
using FluentEmail.Smtp;
using LangChain.Providers.OpenAI;
using Microsoft.SemanticKernel;
using StackExchange.Redis;
using PhoneNumbers;

namespace DigitalCoFounder.BPO
{
    // ========================================================================
    // BACKGROUND WORKERS
    // ========================================================================
    // (All workers are fully implemented – included in your original Phase 3.
    //  For brevity, I am not repeating them here, but they are present in the
    //  final codebase you already have. The only changes are in ApiEndpoints.)
    // ========================================================================

    // ========================================================================
    // API ENDPOINTS (with corrected /api/client/jobs/{id}/accept)
    // ========================================================================
    public static class ApiEndpoints
    {
        public static void MapEndpoints(WebApplication app)
        {
            // Auth endpoints (unchanged)
            app.MapPost("/api/auth/register", async (IRepository<User> userRepo, IUnitOfWork uow, RegisterDto dto) =>
            {
                var user = new User(dto.TenantId, dto.Email, dto.FirstName, dto.LastName, dto.Phone, UserRole.Client);
                user.SetPassword(dto.Password);
                await userRepo.AddAsync(user);
                await uow.SaveChangesAsync();
                return Results.Ok(new { message = "User registered" });
            });

            app.MapPost("/api/auth/login", async (IRepository<User> userRepo, IUnitOfWork uow, LoginDto dto, Config config, IRefreshTokenService refreshTokenService) =>
            {
                var user = await userRepo.Query().FirstOrDefaultAsync(u => u.Email == dto.Email);
                if (user == null || !user.VerifyPassword(dto.Password)) return Results.Unauthorized();
                var token = GenerateJwtToken(user, config);
                var refreshToken = Convert.ToBase64String(RandomNumberGenerator.GetBytes(64));
                var refreshTokenHash = BCrypt.HashPassword(refreshToken);
                user.UpdateRefreshToken(refreshTokenHash, DateTime.UtcNow.AddDays(config.RefreshTokenExpiryDays));
                await userRepo.UpdateAsync(user);
                await uow.SaveChangesAsync();
                return Results.Ok(new { token, refreshToken });
            });

            // Client job board endpoints
            app.MapPost("/api/client/register", async (IRepository<User> userRepo, IUnitOfWork uow, RegisterDto dto) =>
            {
                var user = new User(dto.TenantId, dto.Email, dto.FirstName, dto.LastName, dto.Phone, UserRole.Client);
                user.SetPassword(dto.Password);
                await userRepo.AddAsync(user);
                await uow.SaveChangesAsync();
                return Results.Ok(new { message = "Client registered" });
            });

            app.MapPost("/api/client/login", async (IRepository<User> userRepo, IUnitOfWork uow, LoginDto dto, Config config) =>
            {
                var user = await userRepo.Query().FirstOrDefaultAsync(u => u.Email == dto.Email && u.Role == UserRole.Client);
                if (user == null || !user.VerifyPassword(dto.Password)) return Results.Unauthorized();
                var token = GenerateJwtToken(user, config);
                return Results.Ok(new { token });
            });

            app.MapPost("/api/client/jobs", async (HttpRequest request, IRepository<JobPosting> jobRepo, IUnitOfWork uow, IEventBus eventBus, ITenantContext tenant) =>
            {
                var body = await request.ReadFromJsonAsync<ClientJobPostDto>();
                if (body == null) return Results.BadRequest("Invalid job data");
                var job = new JobPosting(tenant.CurrentTenantId, body.Title, body.Description, body.Budget, "ClientPortal", "");
                await jobRepo.AddAsync(job);
                await uow.SaveChangesAsync();
                await eventBus.PublishAsync(new NewJobEvent(job.Id, job.TenantId));
                return Results.Ok(new { jobId = job.Id, message = "Job posted. AI proposal will be generated." });
            });

            app.MapGet("/api/client/jobs/{id}/proposal", async (int id, DecisionEngineService decision, IRepository<JobPosting> jobRepo) =>
            {
                var job = await jobRepo.GetByIdAsync(id);
                if (job == null) return Results.NotFound();
                var proposal = await decision.GenerateProposalAsync(job, job.Budget * 0.95m);
                return Results.Ok(new { proposalText = proposal.ProposalText, estimatedPrice = proposal.EstimatedPrice, timelineDays = proposal.TimelineDays });
            }).RequireAuthorization();

            // ====================================================================
            // FIXED: /api/client/jobs/{id}/accept – injects IRepository<Client> directly
            // ====================================================================
            app.MapPost("/api/client/jobs/{id}/accept", async (
                int id,
                IRepository<JobPosting> jobRepo,
                IRepository<Client> clientRepo,
                IRepository<Project> projectRepo,
                IUnitOfWork uow,
                ITenantContext tenant,
                IEventBus eventBus,
                Config config) =>
            {
                var job = await jobRepo.GetByIdAsync(id);
                if (job == null) return Results.NotFound();

                var client = new Client(tenant.CurrentTenantId, "", job.Title, "Contact", "client@example.com", "");
                await clientRepo.AddAsync(client);
                await uow.SaveChangesAsync();

                var project = new Project(tenant.CurrentTenantId, client.Id, job.Title, job.Description,
                                          ServiceType.SoftwareDevelopment, job.Budget, WorkType.Outsourced, config);
                project.AddMilestone("Project Completion", job.Budget, "Deliver working software");
                await projectRepo.AddAsync(project);
                await uow.SaveChangesAsync();

                await eventBus.PublishAsync(new ProjectCreatedEvent(project.Id, tenant.CurrentTenantId));
                job.AcceptProposal("");
                await uow.SaveChangesAsync();

                return Results.Ok(new { projectId = project.Id, depositAmount = job.Budget * 0.3m });
            }).RequireAuthorization();

            app.MapGet("/api/client/projects", async (IRepository<Project> projectRepo, ITenantContext tenant) =>
            {
                var projects = await projectRepo.Query().Where(p => p.TenantId == tenant.CurrentTenantId).ToListAsync();
                return Results.Ok(projects.Select(p => new { p.Id, p.Name, p.Description, p.TotalPrice, p.Status, p.DepositPaid }));
            }).RequireAuthorization();

            app.MapPost("/api/client/projects/{id}/approve-milestone", async (int id, IRepository<Project> projectRepo, IUnitOfWork uow, EscrowPaymentService escrow) =>
            {
                var project = await projectRepo.GetByIdAsync(id);
                if (project == null) return Results.NotFound();
                var nextMilestone = project.Milestones.FirstOrDefault(m => !m.Released);
                if (nextMilestone == null) return Results.BadRequest("No pending milestones");
                await escrow.ReleaseMilestonePaymentAsync(nextMilestone.Id);
                return Results.Ok();
            }).RequireAuthorization();

            // Payments webhooks
            app.MapPost("/api/webhooks/grey", async (HttpRequest request, IPaymentService paymentService) =>
            {
                var payload = await new StreamReader(request.Body).ReadToEndAsync();
                var signature = request.Headers["X-Grey-Signature"].ToString();
                await paymentService.HandleGreyWebhookAsync(payload, signature);
                return Results.Ok();
            }).DisableRateLimiting();

            app.MapPost("/api/webhooks/payfast/itn", async (HttpRequest request, IPaymentService paymentService) =>
            {
                await paymentService.HandlePayFastItnAsync(request);
                return Results.Ok();
            }).DisableRateLimiting();

            // Voice endpoints
            app.MapGet("/api/voice/onboarding/{journeyId}/twiml", async (int journeyId, IRepository<ClientJourney> journeyRepo, IRepository<Client> clientRepo) =>
            {
                var journey = await journeyRepo.GetByIdAsync(journeyId);
                if (journey == null) return Results.NotFound();
                var client = await clientRepo.GetByIdAsync(journey.ClientId);
                var companyName = client?.CompanyName ?? "customer";
                var twiml = $@"<Response><Gather input=""speech"" action=""/api/voice/webhook/{journeyId}"" method=""POST"" speechTimeout=""auto""><Say>Welcome to AutoBPO, {companyName}. Tell me about your project.</Say></Gather><Say>Goodbye.</Say></Response>";
                return Results.Content(twiml, "text/xml");
            });

            app.MapPost("/api/voice/webhook/{journeyId}", async (int journeyId, HttpRequest request, DecisionEngineService decision, IRepository<ClientJourney> journeyRepo, IEmailService email) =>
            {
                var form = await request.ReadFormAsync();
                var speechResult = form["SpeechResult"].ToString();
                if (string.IsNullOrEmpty(speechResult)) return Results.Content(@"<Response><Say>I didn't hear anything. Goodbye.</Say></Response>", "text/xml");
                var intent = await decision.InterpretClientIntentAsync(speechResult);
                return Results.Content($@"<Response><Say>{intent.FollowUpQuestion}</Say><Redirect>/api/voice/onboarding/{journeyId}/twiml</Redirect></Response>", "text/xml");
            });

            // Job endpoints (for dashboard)
            app.MapGet("/api/jobs", async (IRepository<JobPosting> repo, int? limit = 50) =>
            {
                var jobs = await repo.QueryNoTracking().OrderByDescending(j => j.ScrapedAt).Take(limit ?? 50).ToListAsync();
                return Results.Ok(jobs);
            }).RequireAuthorization();

            app.MapPost("/api/jobs/{id}/generate-proposal", async (int id, DecisionEngineService decision, IRepository<JobPosting> jobRepo) =>
            {
                var job = await jobRepo.GetByIdAsync(id);
                if (job == null) return Results.NotFound();
                var proposal = await decision.GenerateProposalAsync(job, job.Budget * 0.95m);
                return Results.Ok(new { proposalText = proposal.ProposalText, estimatedPrice = proposal.EstimatedPrice, timelineDays = proposal.TimelineDays });
            }).RequireAuthorization();

            // Dashboard endpoints (cached)
            app.MapGet("/api/dashboard/overview", async (CacheService cache, ITenantContext tenant, IRepository<Project> projectRepo, IRepository<Lead> leadRepo, IRepository<Bid> bidRepo, IRepository<JobPosting> jobRepo) =>
            {
                var tenantId = tenant.CurrentTenantId;
                var key = $"dashboard_overview_{tenantId}";
                return await cache.GetOrSetAsync(key, async () =>
                {
                    var activeProjects = await projectRepo.Query().CountAsync(p => p.Status != ProjectStatus.Completed && p.Status != ProjectStatus.Cancelled);
                    var monthlyRevenue = await projectRepo.Query().Where(p => p.CompletedAt != null && p.CompletedAt.Value.Month == DateTime.UtcNow.Month).SumAsync(p => p.TotalPrice);
                    var bidsPlaced = await bidRepo.Query().CountAsync();
                    var jobsScraped = await jobRepo.Query().CountAsync(j => j.ScrapedAt > DateTime.UtcNow.AddDays(-7));
                    return new { activeProjects, monthlyRevenue, bidsPlaced, jobsScraped };
                });
            }).RequireAuthorization();

            app.MapGet("/api/dashboard/revenue", async (CacheService cache, ITenantContext tenant, IRepository<Project> repo) =>
            {
                var tenantId = tenant.CurrentTenantId;
                var key = $"dashboard_revenue_{tenantId}";
                return await cache.GetOrSetAsync(key, async () =>
                {
                    var last6Months = Enumerable.Range(0, 6).Select(i => DateTime.UtcNow.AddMonths(-i)).Reverse().ToList();
                    var revenueByMonth = new List<object>();
                    foreach (var month in last6Months)
                    {
                        var total = await repo.Query().Where(p => p.CompletedAt != null && p.CompletedAt.Value.Year == month.Year && p.CompletedAt.Value.Month == month.Month).SumAsync(p => p.TotalPrice);
                        revenueByMonth.Add(new { month = month.ToString("MMM yyyy"), revenue = total });
                    }
                    return revenueByMonth;
                });
            }).RequireAuthorization();

            static string GenerateJwtToken(User user, Config config)
            {
                var key = Encoding.UTF8.GetBytes(config.JwtSecret);
                var tokenHandler = new JwtSecurityTokenHandler();
                var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, user.Id.ToString()), new(ClaimTypes.Email, user.Email), new(ClaimTypes.Role, user.Role.ToString()), new("tenant_id", user.TenantId) };
                var tokenDescriptor = new SecurityTokenDescriptor { Subject = new ClaimsIdentity(claims), Expires = DateTime.UtcNow.AddHours(config.JwtExpiryHours), Issuer = config.JwtIssuer, Audience = config.JwtAudience, SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(key), SecurityAlgorithms.HmacSha256Signature) };
                return tokenHandler.CreateEncodedJwt(tokenDescriptor);
            }
        }
    }

    public record ClientJobPostDto(string Title, string Description, decimal Budget, string ServiceType);

    // ========================================================================
    // VALIDATORS
    // ========================================================================
    public class CreateLeadDtoValidator : AbstractValidator<CreateLeadDto>
    {
        private readonly PhoneNumberUtil _phoneUtil = PhoneNumberUtil.GetInstance();
        private readonly Config _config;
        public CreateLeadDtoValidator(Config config) { _config = config; RuleFor(x => x.CompanyName).NotEmpty(); RuleFor(x => x.Email).EmailAddress(); RuleFor(x => x.Phone).Must(BeValidInternationalPhone); RuleFor(x => x.Budget).GreaterThan(0).Must(b => b >= _config.MinJobBudgetUsd); RuleFor(x => x.Score).InclusiveBetween(0, 100); }
        private bool BeValidInternationalPhone(string phone) { try { return _phoneUtil.IsValidNumber(_phoneUtil.Parse(phone, null)); } catch { return false; } }
    }
    public class CreateProjectDtoValidator : AbstractValidator<CreateProjectDto> { public CreateProjectDtoValidator() { RuleFor(x => x.Name).NotEmpty(); RuleFor(x => x.TotalPrice).GreaterThan(0); RuleFor(x => x.Milestones).NotEmpty(); } }
    public class CreatePaymentIntentDtoValidator : AbstractValidator<CreatePaymentIntentDto> { public CreatePaymentIntentDtoValidator() { RuleFor(x => x.ProjectId).GreaterThan(0); RuleFor(x => x.Amount).GreaterThan(0); RuleFor(x => x.Currency).NotEmpty().Length(3); RuleFor(x => x.PaymentMethod).NotEmpty(); } }

    // ========================================================================
    // PROGRAM.CS (unchanged)
    // ========================================================================
    public class Program
    {
        public static async Task Main(string[] args)
        {
            Log.Logger = new LoggerConfiguration().WriteTo.Console().WriteTo.File("logs/autobpo-.txt", rollingInterval: RollingInterval.Day).CreateLogger();
            try
            {
                var builder = WebApplication.CreateBuilder(args);
                builder.Host.UseSerilog();
                var configuration = new ConfigurationBuilder().SetBasePath(Directory.GetCurrentDirectory()).AddJsonFile("appsettings.json", optional: false).AddEnvironmentVariables().Build();
                var config = configuration.Get<Config>() ?? new Config();
                if (string.IsNullOrWhiteSpace(config.JwtSecret) || config.JwtSecret.Length < 32) throw new InvalidOperationException("JWT_SECRET must be at least 32 characters.");

                builder.Services.AddSingleton(config);
                builder.Services.AddHttpContextAccessor();
                builder.Services.AddSingleton<ITenantContext, TenantContext>();
                builder.Services.AddSingleton<IRefreshTokenService, RefreshTokenService>();
                builder.Services.AddSingleton<IEventBus, RabbitMQEventBus>();
                builder.Services.AddDbContext<AppDbContext>(options => options.UseNpgsql(config.ClientsConnection, o => o.UseVector()));
                builder.Services.AddScoped<IUnitOfWork, UnitOfWork>();
                builder.Services.AddScoped(typeof(IRepository<>), typeof(Repository<>));
                builder.Services.AddStackExchangeRedisCache(options => options.Configuration = config.RedisConnection);
                builder.Services.AddSingleton<IConnectionMultiplexer>(sp => ConnectionMultiplexer.Connect(config.RedisConnection));
                builder.Services.AddScoped<CacheService>();
                builder.Services.AddSingleton<Kernel>(sp =>
                {
                    var kernelBuilder = Kernel.CreateBuilder();
                    if (!string.IsNullOrEmpty(config.AzureOpenAIEndpoint) && !string.IsNullOrEmpty(config.AzureOpenAIApiKey) && !string.IsNullOrEmpty(config.AzureOpenAIDeploymentName))
                        kernelBuilder.AddAzureOpenAIChatCompletion(config.AzureOpenAIDeploymentName, config.AzureOpenAIEndpoint, config.AzureOpenAIApiKey);
                    else if (!string.IsNullOrEmpty(config.OpenAIApiKey))
                        kernelBuilder.AddOpenAIChatCompletion(config.OpenAIModel, config.OpenAIApiKey);
                    else throw new InvalidOperationException("No LLM configuration found.");
                    return kernelBuilder.Build();
                });
                if (!string.IsNullOrEmpty(config.OpenAIApiKey) || (!string.IsNullOrEmpty(config.AzureOpenAIEndpoint) && !string.IsNullOrEmpty(config.AzureOpenAIApiKey)))
                    builder.Services.AddSingleton<IEmbeddingModel>(sp => new LangChainEmbeddingAdapter(new OpenAIProvider(config.OpenAIApiKey).CreateEmbeddingModel(config.EmbeddingModel)));
                else builder.Services.AddSingleton<IEmbeddingModel, DummyEmbeddingModel>();
                builder.Services.AddFluentEmail(config.SmtpFrom, config.SmtpUser).AddSmtpSender(config.SmtpServer, config.SmtpPort, config.SmtpUser, config.SmtpPass);
                builder.Services.AddFluentValidationAutoValidation();
                builder.Services.AddValidatorsFromAssemblyContaining<CreateLeadDtoValidator>();
                builder.Services.AddScoped<IValidator<CreateLeadDto>, CreateLeadDtoValidator>();

                // Register all services from Phase 2
                builder.Services.AddScoped<MemoryService>();
                builder.Services.AddScoped<LeadService>();
                builder.Services.AddScoped<DeveloperMarketplaceService>();
                builder.Services.AddScoped<DeveloperOnboardingService>();
                builder.Services.AddScoped<IPaymentService, PaymentService>();
                builder.Services.AddScoped<DecisionEngineService>();
                builder.Services.AddScoped<QAService>();
                builder.Services.AddScoped<CRMAutomationService>();
                builder.Services.AddScoped<VoiceOnboardingService>();
                builder.Services.AddScoped<SocialMediaService>();
                builder.Services.AddScoped<ComplianceService>();
                builder.Services.AddScoped<IEmailService, EmailService>();
                builder.Services.AddScoped<IGitHubService, GitHubService>();
                builder.Services.AddScoped<PayoutAccountService>();
                builder.Services.AddScoped<EscrowPaymentService>();
                builder.Services.AddScoped<SupportTicketService>();
                builder.Services.AddScoped<DeveloperReassignmentService>();
                builder.Services.AddScoped<LearningPathService>();
                builder.Services.AddScoped<PortfolioLibraryService>();
                builder.Services.AddScoped<CommunityService>();
                builder.Services.AddScoped<SubscriptionService>();
                builder.Services.AddScoped<DeveloperMatchingAgent>();
                builder.Services.AddScoped<IEventHandler<NewJobEvent>, NewJobEventHandler>();
                builder.Services.AddScoped<IEventHandler<ProjectCreatedEvent>, ProjectAssignmentHandler>();
                builder.Services.AddScoped<IEventHandler<DeveloperAssignedEvent>, TaskPlanningHandler>();

                // Background workers
                builder.Services.AddHostedService<MultiPlatformJobScraper>();
                builder.Services.AddHostedService<AutoBiddingAgent>();
                builder.Services.AddHostedService<OnboardingAgentService>();
                builder.Services.AddHostedService<ScopeChangeProcessor>();
                builder.Services.AddHostedService<QAProcessor>();
                builder.Services.AddHostedService<DeveloperInactivityChecker>();
                builder.Services.AddHostedService<EventOutboxService>();
                builder.Services.AddHostedService<PricingAgent>();
                builder.Services.AddHostedService<DeveloperMatchingAgent>();
                builder.Services.AddHostedService<EmailNotificationAgent>();

                builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
                {
                    options.TokenValidationParameters = new TokenValidationParameters
                    {
                        ValidateIssuer = true,
                        ValidateAudience = true,
                        ValidateLifetime = true,
                        ValidateIssuerSigningKey = true,
                        ValidIssuer = config.JwtIssuer,
                        ValidAudience = config.JwtAudience,
                        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(config.JwtSecret)),
                        ClockSkew = TimeSpan.Zero
                    };
                });
                builder.Services.AddAuthorization();
                builder.Services.AddAntiforgery();
                builder.Services.AddCors(options => options.AddPolicy("AllowSpecific", policy => policy.WithOrigins(config.AllowedOrigins).AllowAnyMethod().AllowAnyHeader().AllowCredentials()));
                builder.Services.AddRateLimiter(options => options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(httpContext => RateLimitPartition.GetFixedWindowLimiter(partitionKey: httpContext.User.FindFirst("tenant_id")?.Value ?? httpContext.Request.Headers.Host.ToString(), factory: _ => new FixedWindowRateLimiterOptions { PermitLimit = config.MaxRequestsPerMinutePerTenant, Window = TimeSpan.FromMinutes(1) })));
                builder.Services.AddHealthChecks().AddDbContextCheck<AppDbContext>().AddRedis(config.RedisConnection).AddNpgSql(config.ClientsConnection).AddRabbitMQ($"amqp://{config.RabbitMQUser}:{config.RabbitMQPassword}@{config.RabbitMQHost}");
                builder.Services.AddOpenTelemetry().WithTracing(tracing => tracing.AddAspNetCoreInstrumentation().AddHttpClientInstrumentation().AddOtlpExporter(opt => opt.Endpoint = new Uri(config.OtlpEndpoint))).WithMetrics(metrics => metrics.AddAspNetCoreInstrumentation());
                builder.Services.AddMetricServer(options => options.Port = 9090);
                builder.Services.AddSignalR();
                builder.Services.AddEndpointsApiExplorer();
                builder.Services.AddSwaggerGen(c => { c.SwaggerDoc("v23", new OpenApiInfo { Title = "AutoBPO API", Version = "v23.0" }); c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme { In = ParameterLocation.Header, Name = "Authorization", Type = SecuritySchemeType.Http, Scheme = "Bearer" }); });

                var app = builder.Build();
                app.Use(async (context, next) => { context.Response.Headers["X-Content-Type-Options"] = "nosniff"; context.Response.Headers["X-Frame-Options"] = "DENY"; context.Response.Headers["Content-Security-Policy"] = "default-src 'self'"; await next(); });
                app.UseHsts();
                app.UseHttpsRedirection();
                app.UseCors("AllowSpecific");
                app.UseAuthentication();
                app.UseMiddleware<TenantMiddleware>();
                app.UseAuthorization();
                app.UseRateLimiter();
                app.UseHttpMetrics();
                app.UseSerilogRequestLogging();
                app.UseSwagger();
                app.UseSwaggerUI(c => c.SwaggerEndpoint("/swagger/v23/swagger.json", "AutoBPO API v23.0"));
                app.MapHealthChecks("/health");
                app.MapMetrics();
                app.MapHub<ChatHub>("/chathub");
                ApiEndpoints.MapEndpoints(app);
                await new BrowserFetcher().DownloadAsync();
                await app.RunAsync();
            }
            catch (Exception ex) { Log.Fatal(ex, "Application terminated unexpectedly"); }
            finally { Log.CloseAndFlush(); }
        }
    }
}
