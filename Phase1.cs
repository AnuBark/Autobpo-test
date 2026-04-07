// ========================================================================
// Digital Co-Founder BPO Agent v23.0 – PHASE 1 (Infrastructure & Domain)
// ========================================================================
// FINAL – Fully patched, repository tenant fix, all domain models included.
// ========================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using Polly;
using Polly.CircuitBreaker;
using StackExchange.Redis;
using Microsoft.AspNetCore.Http;
using BCrypt.Net;

namespace DigitalCoFounder.BPO
{
    // DomainException
    public class DomainException : Exception
    {
        public DomainException(string message) : base(message) { }
    }

    // Enums
    public enum UserRole { Admin, Developer, Client }
    public enum LeadStatus { New, Contacted, Qualified, ProposalSent, Won, Lost }
    public enum ProjectStatus { Scoping, WaitingDeposit, DepositPaid, InProgress, Review, ClientApproval, Completed, Cancelled }
    public enum PaymentStatus { Pending, Completed, Failed, Refunded }
    public enum EscrowStatus { PendingDeposit, DepositHeld, MilestoneCompleted, ClientApproved, Released, Disputed, Refunded }
    public enum DisputeReason { Quality, Delay, Miscommunication, Other }
    public enum ServiceType { BPO, KPO_Analytics, KPO_Legal, KPO_RnD, KPO_ESG, Offshore_India, Offshore_Philippines, Offshore_China, Hybrid_GCC, SoftwareDevelopment }
    public enum WorkType { AI_Self, Outsourced }
    public enum OnboardingStepStatus { Pending, InProgress, Completed, Failed }
    public enum JourneyStage { Lead, Onboarding, Active, Retention, AtRisk, Churned }
    public enum ScopeChangeStatus { Requested, Approved, Rejected, Implemented }
    public enum TicketPriority { Low, Medium, High, Urgent }
    public enum TicketStatus { Open, InProgress, Resolved, Closed }

    // DTOs & Records
    public record CreateLeadDto(string CompanyName, string ContactName, string Email, string Phone, ServiceType ServiceType, decimal Budget, string Notes, string Source, int Score);
    public record CreateProjectDto(int ClientId, string Name, string Description, ServiceType ServiceType, decimal TotalPrice, WorkType WorkType, List<MilestoneDto> Milestones);
    public record MilestoneDto(string Name, decimal Amount, string Condition);
    public record LeadTransitionDto(LeadStatus NewStatus, string Reason);
    public record CreatePaymentIntentDto(int ProjectId, decimal Amount, string Currency, string PaymentMethod);
    public record BidRequest(int JobId, string Proposal);
    public record ScopeChangeRequestDto(int ProjectId, string Description, decimal? ProposedNewPrice);
    public record SocialMediaPost(string Platform, string Content, string MediaUrl = "");
    public record ChatMessageDto(int ProjectId, string SenderRole, string Message);
    public record SupportTicketDto(int ProjectId, string Subject, string Description, TicketPriority Priority);
    public record CodeDeliveryRequestDto(int ProjectId, int MilestoneId);
    public record RegisterDto(string Email, string Password, string FirstName, string LastName, string Phone, string TenantId);
    public record LoginDto(string Email, string Password);
    public record RefreshTokenDto(string RefreshToken);
    public record DeveloperOnboardDto(string Name, string Email, string Phone, List<string> Skills, decimal HourlyRate, string Country, string ProfileUrl);
    public record CopyPortfolioRequest(string CustomTitle);
    public record CreateCheckoutRequest(int PlanId, string SuccessUrl, string CancelUrl);
    public record GreyPaymentResponse(string PaymentId, string ClientSecret);
    public record GreyWebhookEvent(string PaymentId, string Status, string Metadata);
    public record PayFastItnRequest(string m_payment_id, string pf_payment_id, string payment_status, string amount, string item_name);
    public record Scope(List<string> Features, string KPOType = "", string OffshoreHub = "", string GCCPhase = "");
    public record QaReport(bool Passed, int Score, string Details);
    public record PaginatedResult<T>(int TotalCount, int Page, int PageSize, IEnumerable<T> Items);
    public record LeadCreatedMessage(int LeadId);
    public record PaymentReceivedMessage(int PaymentId, string TransactionId, decimal Amount, string Currency);
    public record ProjectCompletedMessage(int ProjectId);
    public record DeveloperAssignedMessage(int ProjectId, int DeveloperId);
    public record Embedding(float[] Vector);

    // Domain Events
    public interface IDomainEvent { }
    public interface ITenantEvent { string TenantId { get; } }
    public record LeadWonEvent(int LeadId, string TenantId) : IDomainEvent, ITenantEvent;
    public record LeadLostEvent(int LeadId, string Reason, string TenantId) : IDomainEvent, ITenantEvent;
    public record LeadQualifiedEvent(int LeadId, string TenantId) : IDomainEvent, ITenantEvent;
    public record ProposalSentEvent(int LeadId, string TenantId) : IDomainEvent, ITenantEvent;
    public record DepositReceivedEvent(int ProjectId, decimal Amount, string TenantId) : IDomainEvent, ITenantEvent;
    public record ProjectCompletedEvent(int ProjectId, string TenantId) : IDomainEvent, ITenantEvent;
    public record DeveloperAssignedEvent(int ProjectId, int DeveloperId, string TenantId) : IDomainEvent, ITenantEvent;
    public record PaymentCompletedEvent(int PaymentId, string TransactionId, decimal Amount, string Currency, string TenantId) : IDomainEvent, ITenantEvent;
    public record PaymentRefundedEvent(int PaymentId, decimal Amount, string Currency, string TenantId) : IDomainEvent, ITenantEvent;
    public record NewJobEvent(int JobId, string TenantId) : IDomainEvent, ITenantEvent;
    public record ClientOnboardedEvent(int ClientId, string TenantId) : IDomainEvent, ITenantEvent;
    public record ScopeChangeRequestedEvent(int ProjectId, decimal OriginalPrice, decimal NewPrice, string Description) : IDomainEvent, ITenantEvent;
    public record GitHubPushEvent(string RepoName, string CommitSha, DateTime PushedAt) : IDomainEvent, ITenantEvent;
    public record LearningPathCompletedEvent(int LearningPathId, string TenantId) : IDomainEvent, ITenantEvent;
    public record ProjectCreatedEvent(int ProjectId, string TenantId) : IDomainEvent, ITenantEvent;
    public record QAPassedEvent(int ProjectId, int MilestoneId, string TenantId) : IDomainEvent, ITenantEvent;

    // Config Class
    public class Config
    {
        public bool UsePostgres { get; set; } = true;
        public string ClientsConnection { get; set; } = "Host=localhost;Database=bpo_clients;Username=bpo_user;Password=bpo_password";
        public bool UseKeyVault { get; set; } = false;
        public string KeyVaultUrl { get; set; } = "";
        public string JwtSecret { get; set; } = null!;
        public string JwtIssuer { get; set; } = "DigitalCoFounder.BPO";
        public string JwtAudience { get; set; } = "DigitalCoFounder.BPO.API";
        public int JwtExpiryHours { get; set; } = 24;
        public int RefreshTokenExpiryDays { get; set; } = 7;
        public string SmtpServer { get; set; } = "smtp.gmail.com";
        public int SmtpPort { get; set; } = 587;
        public string SmtpUser { get; set; } = "";
        public string SmtpPass { get; set; } = "";
        public string SmtpFrom { get; set; } = "noreply@bpoco.com";
        public string RedisConnection { get; set; } = "localhost:6379,abortConnect=false";
        public string RabbitMQHost { get; set; } = "localhost";
        public string RabbitMQUser { get; set; } = "guest";
        public string RabbitMQPassword { get; set; } = "guest";
        public string RabbitMQDeadLetterExchange { get; set; } = "bpo.events.dlx";
        public int RabbitMQPrefetchCount { get; set; } = 10;
        public string OpenAIApiKey { get; set; } = null!;
        public string OpenAIModel { get; set; } = "gpt-4o-mini";
        public string AzureOpenAIEndpoint { get; set; } = "";
        public string AzureOpenAIDeploymentName { get; set; } = "";
        public string AzureOpenAIApiKey { get; set; } = null!;
        public bool EnableRAG { get; set; } = true;
        public string EmbeddingModel { get; set; } = "text-embedding-3-small";
        public bool UseHNSWIndex { get; set; } = true;
        public int MemoryRetrievalLimit { get; set; } = 5;
        public int FeedbackLoopDays { get; set; } = 30;
        public decimal BaseCommissionRate { get; set; } = 0.15m;
        public string QACriteria { get; set; } = "Lighthouse score > 80, no console errors, responsive design, no security issues";
        public decimal GCCRampFee { get; set; } = 49500m;
        public string PublicUrl { get; set; } = "https://yourdomain.com";
        public string[] AllowedOrigins { get; set; } = new[] { "https://localhost:3000" };
        public string OtlpEndpoint { get; set; } = "http://jaeger:4317";
        public int DefaultPageSize { get; set; } = 20;
        public int MaxRequestsPerMinutePerTenant { get; set; } = 200;
        public decimal ReinvestmentRate { get; set; } = 0.30m;
        public decimal OperatingExpensesRate { get; set; } = 0.10m;
        public decimal PlatformCommissionRate { get; set; } = 0.15m;
        public decimal AffiliateCommissionRate { get; set; } = 0.10m;
        public Dictionary<ServiceType, decimal> BaseRates { get; set; } = new()
        {
            [ServiceType.BPO] = 25m, [ServiceType.KPO_Analytics] = 45m, [ServiceType.KPO_Legal] = 60m,
            [ServiceType.KPO_RnD] = 55m, [ServiceType.KPO_ESG] = 70m, [ServiceType.Offshore_India] = 15m,
            [ServiceType.Offshore_Philippines] = 18m, [ServiceType.Offshore_China] = 20m, [ServiceType.Hybrid_GCC] = 35m,
            [ServiceType.SoftwareDevelopment] = 50m
        };
        public decimal VolumeDiscountThreshold { get; set; } = 1000m;
        public decimal VolumeDiscountRate { get; set; } = 0.10m;
        public decimal ComplexityMultiplierMax { get; set; } = 1.5m;
        public decimal UrgencyMultiplierMax { get; set; } = 1.3m;
        public int RetryCount { get; set; } = 3;
        public int CircuitBreakerFailureThreshold { get; set; } = 5;
        public int CircuitBreakerDurationSeconds { get; set; } = 30;
        public decimal MinJobBudgetUsd { get; set; } = 500m;
        public string DashboardTitle { get; set; } = "AutoBPO 2026";

        public string UpworkClientId { get; set; } = "";
        public string UpworkClientSecret { get; set; } = "";
        public string FiverrScraperApiKey { get; set; } = "";
        public string PeoplePerHourApiKey { get; set; } = "";
        public int JobScrapeIntervalMinutes { get; set; } = 360;
        public string[] UpworkSearchKeywords { get; set; } = new[] { "BPO", "KPO", "virtual assistant", "data entry", "analytics", "offshore", "developer" };

        public string TwilioAccountSid { get; set; } = "";
        public string TwilioAuthToken { get; set; } = "";
        public string TwilioPhoneNumber { get; set; } = "";

        public string TwitterBearerToken { get; set; } = "";
        public string LinkedInAccessToken { get; set; } = "";
        public string FacebookPageAccessToken { get; set; } = "";

        public decimal DeveloperMinHourlyRate { get; set; } = 8m;
        public decimal DeveloperMaxHourlyRate { get; set; } = 30m;
        public int DeveloperVerificationThreshold { get; set; } = 80;

        public string GitHubToken { get; set; } = "";
        public string GitHubOrganization { get; set; } = "";
        public string GitHubRepoBaseName { get; set; } = "autobpo-project";

        public decimal MilestoneReleaseThreshold { get; set; } = 0.1m;

        public string ChatHubUrl { get; set; } = "/chathub";
        public int SupportAutoEscalateHours { get; set; } = 24;

        public bool EnableComplianceLogging { get; set; } = true;
        public int DataRetentionDays { get; set; } = 365;
        public string ComplianceContactEmail { get; set; } = "compliance@autobpo.com";
        public string[] AllowedJurisdictions { get; set; } = new[] { "ZA", "US", "GB", "EU" };

        public string GreyApiBaseUrl { get; set; } = "https://api.grey.co/v1";
        public string GreyApiKey { get; set; } = "";
        public string GreyWebhookSecret { get; set; } = "";
        public string PayFastMerchantId { get; set; } = "";
        public string PayFastMerchantKey { get; set; } = "";
        public string PayFastPassphrase { get; set; } = "";
    }

    // Tenant Context & Middleware
    public interface ITenantContext { string CurrentTenantId { get; set; } }
    public class TenantContext : ITenantContext
    {
        private static readonly AsyncLocal<string> _current = new();
        public string CurrentTenantId { get => _current.Value ?? "system"; set => _current.Value = value; }
    }

    public class TenantMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly ILogger<TenantMiddleware> _logger;
        public TenantMiddleware(RequestDelegate next, ILogger<TenantMiddleware> logger) { _next = next; _logger = logger; }
        public async Task InvokeAsync(HttpContext context, ITenantContext tenantContext)
        {
            var tenantId = context.User.FindFirst("tenant_id")?.Value;
            if (!string.IsNullOrEmpty(tenantId)) { tenantContext.CurrentTenantId = tenantId; _logger.LogDebug("Set tenant context to {TenantId}", tenantId); }
            else { _logger.LogDebug("No tenant found in claims, using system tenant"); }
            await _next(context);
        }
    }

    // Embedding Model
    public interface IEmbeddingModel { Task<Embedding> GenerateEmbeddingAsync(string text, CancellationToken cancellationToken = default); bool IsRealImplementation { get; } }
    public class LangChainEmbeddingAdapter : IEmbeddingModel
    {
        private readonly LangChain.Providers.IEmbeddingModel _inner;
        public bool IsRealImplementation => true;
        public LangChainEmbeddingAdapter(LangChain.Providers.IEmbeddingModel inner) => _inner = inner;
        public async Task<Embedding> GenerateEmbeddingAsync(string text, CancellationToken cancellationToken = default)
        {
            var result = await _inner.GenerateEmbeddingAsync(text, cancellationToken);
            return new Embedding(result.Vector.ToArray());
        }
    }
    public class DummyEmbeddingModel : IEmbeddingModel
    {
        private readonly ILogger<DummyEmbeddingModel> _logger;
        public bool IsRealImplementation => false;
        public DummyEmbeddingModel(ILogger<DummyEmbeddingModel> logger) => _logger = logger;
        public async Task<Embedding> GenerateEmbeddingAsync(string text, CancellationToken cancellationToken = default)
        {
            _logger.LogWarning("DummyEmbeddingModel used – no real embedding service configured. Returning random vector.");
            var random = new Random();
            var vector = Enumerable.Range(0, 1536).Select(_ => (float)random.NextDouble()).ToArray();
            return new Embedding(vector);
        }
    }

    // Domain Models
    public abstract class AggregateRoot
    {
        private List<IDomainEvent> _events = new();
        public IReadOnlyCollection<IDomainEvent> DomainEvents => _events.AsReadOnly();
        protected void AddDomainEvent(IDomainEvent eventItem) { _events ??= new List<IDomainEvent>(); _events.Add(eventItem); }
        public void ClearEvents() => _events?.Clear();
    }

    public class Lead : AggregateRoot
    {
        public int Id { get; private set; }
        public string TenantId { get; private set; }
        public string CompanyName { get; private set; }
        public string ContactName { get; private set; }
        public string Email { get; private set; }
        public string Phone { get; private set; }
        public LeadStatus Status { get; private set; }
        public ServiceType ServiceType { get; private set; }
        public string Source { get; private set; }
        public decimal Budget { get; private set; }
        public string Notes { get; private set; }
        public DateTime CreatedAt { get; private set; }
        public DateTime? ConvertedAt { get; private set; }
        public int? ConvertedClientId { get; private set; }
        public string AssignedTo { get; private set; }
        public int Score { get; private set; }
        public string ReasonLost { get; private set; }
        public bool IsDataRetentionExpired { get; private set; }
        public DateTime? ConsentGivenAt { get; private set; }

        private Lead() { }
        public Lead(string tenantId, string companyName, string contactName, string email, string phone, ServiceType serviceType, decimal budget, string notes, string source, int score)
        {
            TenantId = tenantId; CompanyName = companyName; ContactName = contactName; Email = email; Phone = phone;
            ServiceType = serviceType; Budget = budget; Notes = notes; Source = source; Status = LeadStatus.New;
            CreatedAt = DateTime.UtcNow; Score = score;
        }
        public void TransitionTo(LeadStatus newStatus, string reason = null)
        {
            if (!CanTransitionTo(newStatus)) throw new DomainException($"Invalid transition from {Status} to {newStatus}");
            Status = newStatus;
            if (newStatus == LeadStatus.Won) { ConvertedAt = DateTime.UtcNow; AddDomainEvent(new LeadWonEvent(Id, TenantId)); }
            else if (newStatus == LeadStatus.Lost) { ReasonLost = reason; AddDomainEvent(new LeadLostEvent(Id, reason, TenantId)); }
            else if (newStatus == LeadStatus.Qualified) AddDomainEvent(new LeadQualifiedEvent(Id, TenantId));
            else if (newStatus == LeadStatus.ProposalSent) AddDomainEvent(new ProposalSentEvent(Id, TenantId));
        }
        public void MarkAsDataRetentionExpired() { IsDataRetentionExpired = true; }
        private bool CanTransitionTo(LeadStatus newStatus) => (Status, newStatus) switch
        {
            (LeadStatus.New, LeadStatus.Contacted) => true,
            (LeadStatus.Contacted, LeadStatus.Qualified) => true,
            (LeadStatus.Qualified, LeadStatus.ProposalSent) => true,
            (LeadStatus.ProposalSent, LeadStatus.Won) => true,
            (LeadStatus.ProposalSent, LeadStatus.Lost) => true,
            _ => false
        };
    }

    public class Project : AggregateRoot
    {
        public int Id { get; private set; }
        public string TenantId { get; private set; }
        public int ClientId { get; private set; }
        public string Name { get; private set; }
        public string Description { get; private set; }
        public ServiceType ServiceType { get; private set; }
        public ProjectStatus Status { get; private set; }
        public decimal TotalPrice { get; private set; }
        public decimal DepositPaid { get; private set; }
        public decimal PlatformCommission { get; private set; }
        public DateTime CreatedAt { get; private set; }
        public DateTime? StartedAt { get; private set; }
        public DateTime? CompletedAt { get; private set; }
        public int? AssignedDeveloperId { get; private set; }
        public WorkType WorkType { get; private set; }
        public List<Milestone> Milestones { get; private set; } = new();
        public bool IsDataRetentionExpired { get; private set; }
        public Client Client { get; private set; } = null!;

        private Project() { }
        public Project(string tenantId, int clientId, string name, string description, ServiceType serviceType, decimal totalPrice, WorkType workType, Config config)
        {
            TenantId = tenantId; ClientId = clientId; Name = name; Description = description; ServiceType = serviceType;
            TotalPrice = totalPrice; WorkType = workType; Status = ProjectStatus.Scoping; CreatedAt = DateTime.UtcNow;
            RecalculateCommission(config);
        }
        private void RecalculateCommission(Config config) => PlatformCommission = TotalPrice * config.PlatformCommissionRate;
        public void AddMilestone(string name, decimal amount, string condition) => Milestones.Add(new Milestone(Id, name, amount, condition));
        public void AddDeposit(decimal amount) { if (amount <= 0) throw new DomainException("Deposit must be positive"); DepositPaid += amount; if (DepositPaid >= TotalPrice * 0.3m) Status = ProjectStatus.DepositPaid; AddDomainEvent(new DepositReceivedEvent(Id, amount, TenantId)); }
        public void Start() { Status = ProjectStatus.InProgress; StartedAt = DateTime.UtcNow; }
        public void MarkQAPassed() { Status = ProjectStatus.ClientApproval; }
        public void Complete() { Status = ProjectStatus.Completed; CompletedAt = DateTime.UtcNow; AddDomainEvent(new ProjectCompletedEvent(Id, TenantId)); }
        public void AssignDeveloper(int developerId) { AssignedDeveloperId = developerId; Start(); AddDomainEvent(new DeveloperAssignedEvent(Id, developerId, TenantId)); }
        public void AdjustPrice(decimal newPrice, Config config) { TotalPrice = newPrice; RecalculateCommission(config); }
        public void MarkAsDataRetentionExpired() { IsDataRetentionExpired = true; }
    }

    public class Milestone
    {
        public int Id { get; private set; }
        public int ProjectId { get; private set; }
        public string Name { get; private set; }
        public decimal Amount { get; private set; }
        public bool Released { get; private set; }
        public string ReleaseCondition { get; private set; }
        private Milestone() { }
        public Milestone(int projectId, string name, decimal amount, string condition) { ProjectId = projectId; Name = name; Amount = amount; ReleaseCondition = condition; Released = false; }
        public void Release() => Released = true;
    }

    public class Payment : AggregateRoot
    {
        public int Id { get; private set; }
        public string TenantId { get; private set; }
        public int ProjectId { get; private set; }
        public decimal Amount { get; private set; }
        public string Currency { get; private set; }
        public PaymentStatus Status { get; private set; }
        public string TransactionId { get; private set; }
        public string PaymentMethod { get; private set; }
        public DateTime CreatedAt { get; private set; }
        public DateTime? CompletedAt { get; private set; }
        public EscrowStatus EscrowStatus { get; private set; } = EscrowStatus.PendingDeposit;
        public DateTime? DepositReceivedAt { get; private set; }
        public DateTime? ReleasedAt { get; private set; }
        public int? DisputedBy { get; private set; }
        public DisputeReason? DisputeReason { get; private set; }
        public string? DisputeResolution { get; private set; }

        private Payment() { }
        public Payment(string tenantId, int projectId, decimal amount, string currency, string paymentMethod, string transactionId)
        {
            TenantId = tenantId; ProjectId = projectId; Amount = amount; Currency = currency; PaymentMethod = paymentMethod; TransactionId = transactionId;
            Status = PaymentStatus.Pending; CreatedAt = DateTime.UtcNow;
        }
        public void MarkCompleted() { Status = PaymentStatus.Completed; CompletedAt = DateTime.UtcNow; AddDomainEvent(new PaymentCompletedEvent(Id, TransactionId, Amount, Currency, TenantId)); }
        public void MarkFailed(string reason) { Status = PaymentStatus.Failed; }
        public void MarkDepositHeld() { EscrowStatus = EscrowStatus.DepositHeld; DepositReceivedAt = DateTime.UtcNow; }
        public void MarkReleased() { EscrowStatus = EscrowStatus.Released; ReleasedAt = DateTime.UtcNow; }
    }

    public class Developer : AggregateRoot
    {
        public int Id { get; private set; }
        public string TenantId { get; private set; }
        public string UserId { get; private set; }
        public string DisplayName { get; private set; }
        public string Email { get; private set; }
        public string Phone { get; private set; }
        public List<string> Skills { get; private set; } = new();
        public decimal HourlyRate { get; private set; }
        public double Rating { get; private set; }
        public int CompletedProjects { get; private set; }
        public double AvgCompletionDays { get; private set; }
        public bool IsAvailable { get; private set; }
        public DateTime CreatedAt { get; private set; }
        public string PayoutAccountId { get; private set; } = "";
        public bool Verified { get; private set; }
        public int Score { get; private set; }

        private Developer() { }
        public Developer(string tenantId, string userId, string displayName, string email, string phone, List<string> skills, decimal hourlyRate)
        {
            TenantId = tenantId; UserId = userId; DisplayName = displayName; Email = email; Phone = phone; Skills = skills;
            HourlyRate = hourlyRate; Rating = 0; CompletedProjects = 0; AvgCompletionDays = 0; IsAvailable = true; CreatedAt = DateTime.UtcNow;
            Verified = false; Score = 0;
        }
        public void UpdateRating(double newRating) { Rating = newRating; }
        public void AddCompletedProject(int projectId, int completionDays) { CompletedProjects++; AvgCompletionDays = ((AvgCompletionDays * (CompletedProjects - 1)) + completionDays) / CompletedProjects; }
        public void MarkVerified() => Verified = true;
        public void UpdateScore(int newScore) => Score = newScore;
        public void SetPayoutAccount(string accountId) => PayoutAccountId = accountId;
    }

    public class Client : AggregateRoot
    {
        public int Id { get; private set; }
        public string TenantId { get; private set; }
        public string UserId { get; private set; }
        public string CompanyName { get; private set; }
        public string ContactName { get; private set; }
        public string Email { get; private set; }
        public string Phone { get; private set; }
        public decimal TotalSpent { get; private set; }
        public int CompletedProjects { get; private set; }
        public DateTime CreatedAt { get; private set; }
        public DateTime? LastActivity { get; private set; }
        private Client() { }
        public Client(string tenantId, string userId, string companyName, string contactName, string email, string phone)
        {
            TenantId = tenantId; UserId = userId; CompanyName = companyName; ContactName = contactName; Email = email; Phone = phone;
            TotalSpent = 0; CompletedProjects = 0; CreatedAt = DateTime.UtcNow; LastActivity = DateTime.UtcNow;
        }
        public void AddSpent(decimal amount) => TotalSpent += amount;
        public void AddCompletedProject() => CompletedProjects++;
        public void UpdateLastActivity() => LastActivity = DateTime.UtcNow;
    }

    public class JobPosting : AggregateRoot
    {
        public int Id { get; private set; }
        public string TenantId { get; private set; }
        public string Title { get; private set; }
        public string Description { get; private set; }
        public decimal Budget { get; private set; }
        public string Platform { get; private set; }
        public string Url { get; private set; }
        public string UrlHash { get; private set; }
        public DateTime ScrapedAt { get; private set; }
        public DateTime? AcceptedAt { get; private set; }
        public string? AcceptedProposal { get; private set; }

        private JobPosting() { }
        public JobPosting(string tenantId, string title, string description, decimal budget, string platform, string url)
        {
            TenantId = tenantId; Title = title; Description = description; Budget = budget; Platform = platform; Url = url; ScrapedAt = DateTime.UtcNow;
        }
        public void SetUrlHash(string hash) => UrlHash = hash;
        public void AcceptProposal(string proposal) { AcceptedAt = DateTime.UtcNow; AcceptedProposal = proposal; }
    }

    public class Bid : AggregateRoot
    {
        public int Id { get; private set; }
        public string TenantId { get; private set; }
        public int JobId { get; private set; }
        public string Proposal { get; private set; }
        public string ProposalVariantA { get; private set; }
        public string ProposalVariantB { get; private set; }
        public string SelectedVariant { get; private set; }
        public string Status { get; private set; }
        public int WinProbability { get; private set; }
        public DateTime CreatedAt { get; private set; }
        private Bid() { }
        public Bid(string tenantId, int jobId, string variantA, string variantB, string selectedVariant, string selectedProposal)
        {
            TenantId = tenantId; JobId = jobId; ProposalVariantA = variantA; ProposalVariantB = variantB;
            SelectedVariant = selectedVariant; Proposal = selectedProposal; Status = "Pending"; CreatedAt = DateTime.UtcNow;
        }
        public void SetWinProbability(int probability) => WinProbability = probability;
    }

    public class ScopeChangeRequest : AggregateRoot
    {
        public int Id { get; private set; }
        public int ProjectId { get; private set; }
        public string Description { get; private set; }
        public decimal OriginalPrice { get; private set; }
        public decimal NewPrice { get; private set; }
        public ScopeChangeStatus Status { get; private set; }
        public DateTime CreatedAt { get; private set; }
        public DateTime? ResolvedAt { get; private set; }
        private ScopeChangeRequest() { }
        public ScopeChangeRequest(int projectId, string description, decimal originalPrice, decimal newPrice)
        {
            ProjectId = projectId; Description = description; OriginalPrice = originalPrice; NewPrice = newPrice;
            Status = ScopeChangeStatus.Requested; CreatedAt = DateTime.UtcNow;
        }
        public void Approve() { Status = ScopeChangeStatus.Approved; ResolvedAt = DateTime.UtcNow; AddDomainEvent(new ScopeChangeRequestedEvent(ProjectId, OriginalPrice, NewPrice, Description)); }
        public void Reject() { Status = ScopeChangeStatus.Rejected; ResolvedAt = DateTime.UtcNow; }
    }

    public class ClientJourney : AggregateRoot
    {
        public int Id { get; private set; }
        public string TenantId { get; private set; } = string.Empty;
        public int ClientId { get; private set; }
        public JourneyStage Stage { get; private set; } = JourneyStage.Lead;
        public DateTime StartedAt { get; private set; } = DateTime.UtcNow;
        public DateTime? CompletedAt { get; private set; }
        public List<OnboardingStep> Steps { get; private set; } = new();
        public decimal PredictedLTV { get; private set; } = 0;
        public int NurtureSequenceSent { get; private set; } = 0;

        public void AdvanceStage(JourneyStage newStage) { Stage = newStage; if (newStage == JourneyStage.Active) CompletedAt = DateTime.UtcNow; }
        public void AddStep(string title, string description) => Steps.Add(new OnboardingStep(Id, title, description));
        public void UpdateLTV(decimal ltv) => PredictedLTV = ltv;
    }

    public class OnboardingStep
    {
        public int Id { get; private set; }
        public int JourneyId { get; private set; }
        public string Title { get; private set; } = string.Empty;
        public string Description { get; private set; } = string.Empty;
        public OnboardingStepStatus Status { get; private set; } = OnboardingStepStatus.Pending;
        public DateTime? CompletedAt { get; private set; }
        private OnboardingStep() { }
        public OnboardingStep(int journeyId, string title, string description) { JourneyId = journeyId; Title = title; Description = description; }
        public void MarkComplete() { Status = OnboardingStepStatus.Completed; CompletedAt = DateTime.UtcNow; }
    }

    public class NurtureSequence
    {
        public int Id { get; set; }
        public string TenantId { get; set; } = string.Empty;
        public string Trigger { get; set; } = string.Empty;
        public string EmailSubject { get; set; } = string.Empty;
        public string EmailBody { get; set; } = string.Empty;
        public int DelayHours { get; set; }
    }

    public class ProjectRepository
    {
        public int Id { get; set; }
        public int ProjectId { get; set; }
        public string RepoUrl { get; set; } = "";
        public string RepoName { get; set; } = "";
        public DateTime CreatedAt { get; set; }
        public DateTime? LastPushAt { get; set; }
        public string LastCommitSha { get; set; } = "";
    }

    public class MilestonePayment
    {
        public int Id { get; set; }
        public int MilestoneId { get; set; }
        public string PaymentIntentId { get; set; } = "";
        public decimal Amount { get; set; }
        public bool Released { get; set; }
        public DateTime? ReleasedAt { get; set; }
    }

    public class SupportTicket
    {
        public int Id { get; set; }
        public int ProjectId { get; set; }
        public int? DeveloperId { get; set; }
        public string Subject { get; set; } = "";
        public string Description { get; set; } = "";
        public TicketPriority Priority { get; set; }
        public TicketStatus Status { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime? ResolvedAt { get; set; }
    }

    public class ChatMessage
    {
        public int Id { get; set; }
        public int ProjectId { get; set; }
        public string SenderRole { get; set; } = "";
        public string Message { get; set; } = "";
        public DateTime SentAt { get; set; }
    }

    public class MemoryEntry
    {
        public int Id { get; set; }
        public string TenantId { get; set; } = string.Empty;
        public string Key { get; set; } = string.Empty;
        public string Content { get; set; } = string.Empty;
        public NpgsqlVector Embedding { get; set; } = null!;
        public DateTime CreatedAt { get; set; }
    }

    public class EventStoreEntry
    {
        public int Id { get; set; }
        public string EventType { get; set; } = string.Empty;
        public JsonDocument Payload { get; set; } = null!;
        public string TenantId { get; set; } = string.Empty;
        public bool Processed { get; set; }
        public int Retries { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime? ProcessedAt { get; set; }
    }

    public class JobLog
    {
        public int Id { get; set; }
        public string JobName { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public string? Message { get; set; }
        public string? TenantId { get; set; }
        public DateTime ExecutedAt { get; set; }
    }

    public class AgentDecisionLog
    {
        public int Id { get; set; }
        public string AgentName { get; set; } = string.Empty;
        public int? JobId { get; set; }
        public int? LeadId { get; set; }
        public int? ProjectId { get; set; }
        public string Decision { get; set; } = string.Empty;
        public decimal Score { get; set; }
        public string Reason { get; set; } = string.Empty;
        public decimal ExpectedProfit { get; set; }
        public decimal Confidence { get; set; }
        public JsonDocument? Metadata { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }

    public class Dispute
    {
        public int Id { get; set; }
        public int PaymentId { get; set; }
        public int RaisedByUserId { get; set; }
        public DisputeReason Reason { get; set; }
        public string Description { get; set; } = "";
        public string? Resolution { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime? ResolvedAt { get; set; }
    }

    public class LearningPath : AggregateRoot
    {
        public int Id { get; private set; }
        public string TenantId { get; private set; }
        public string UserId { get; private set; }
        public int Week { get; private set; }
        public string Title { get; private set; }
        public string Description { get; private set; }
        public bool Completed { get; private set; }
        public DateTime? CompletedAt { get; private set; }
        public int Order { get; private set; }
        private LearningPath() { }
        public LearningPath(string tenantId, string userId, int week, string title, string description, int order)
        {
            TenantId = tenantId; UserId = userId; Week = week; Title = title; Description = description; Order = order;
            Completed = false;
        }
        public void MarkComplete() { Completed = true; CompletedAt = DateTime.UtcNow; AddDomainEvent(new LearningPathCompletedEvent(Id, TenantId)); }
    }

    public class PortfolioItem
    {
        public int Id { get; set; }
        public string TenantId { get; set; } = "system";
        public string Title { get; set; }
        public string Description { get; set; }
        public string ImageUrl { get; set; }
        public string TechStack { get; set; }
        public string Client { get; set; }
        public bool IsPublic { get; set; } = true;
    }

    public class UserPortfolio
    {
        public int Id { get; set; }
        public string TenantId { get; set; }
        public string UserId { get; set; }
        public int OriginalPortfolioId { get; set; }
        public string CustomTitle { get; set; }
        public DateTime CreatedAt { get; set; }
    }

    public class LiveEvent
    {
        public int Id { get; set; }
        public string TenantId { get; set; } = "system";
        public string Title { get; set; }
        public DateTime StartTime { get; set; }
        public string MeetingLink { get; set; }
        public string Description { get; set; }
    }

    public class SubscriptionPlan
    {
        public int Id { get; set; }
        public string TenantId { get; set; } = "system";
        public string Name { get; set; }
        public string PriceId { get; set; } = "";
        public decimal MonthlyPrice { get; set; }
        public bool HasAIBidding { get; set; }
        public bool HasDeveloperNetwork { get; set; }
        public bool HasCoaching { get; set; }
    }

    public class UserSubscription
    {
        public int Id { get; set; }
        public string TenantId { get; set; }
        public string UserId { get; set; }
        public int PlanId { get; set; }
        public string ExternalSubscriptionId { get; set; }
        public DateTime ExpiresAt { get; set; }
        public bool IsActive { get; set; }
    }

    public class User
    {
        public int Id { get; set; }
        public string TenantId { get; set; }
        public string Email { get; set; }
        public string PasswordHash { get; set; }
        public UserRole Role { get; set; }
        public string FirstName { get; set; }
        public string LastName { get; set; }
        public string Phone { get; set; }
        public bool IsActive { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime? LastLogin { get; set; }
        public string RefreshTokenHash { get; set; }
        public DateTime? RefreshTokenExpiry { get; set; }
        public string ExternalCustomerId { get; set; }
        public string SubscriptionStatus { get; set; }

        public User(string tenantId, string email, string firstName, string lastName, string phone, UserRole role)
        {
            TenantId = tenantId;
            Email = email;
            FirstName = firstName;
            LastName = lastName;
            Phone = phone;
            Role = role;
            IsActive = true;
            CreatedAt = DateTime.UtcNow;
            SubscriptionStatus = "trial";
        }
        public void SetPassword(string password) => PasswordHash = BCrypt.HashPassword(password);
        public bool VerifyPassword(string password) => BCrypt.Verify(password, PasswordHash);
        public void UpdateRefreshToken(string? tokenHash, DateTime? expiry) { RefreshTokenHash = tokenHash; RefreshTokenExpiry = expiry; }
    }

    // AppDbContext
    public class AppDbContext : DbContext
    {
        public DbSet<User> Users { get; set; } = null!;
        public DbSet<Lead> Leads { get; set; } = null!;
        public DbSet<Project> Projects { get; set; } = null!;
        public DbSet<Milestone> Milestones { get; set; } = null!;
        public DbSet<Client> Clients { get; set; } = null!;
        public DbSet<Developer> Developers { get; set; } = null!;
        public DbSet<Payment> Payments { get; set; } = null!;
        public DbSet<JobPosting> JobPostings { get; set; } = null!;
        public DbSet<Bid> Bids { get; set; } = null!;
        public DbSet<ScopeChangeRequest> ScopeChangeRequests { get; set; } = null!;
        public DbSet<ClientJourney> Journeys { get; set; } = null!;
        public DbSet<MemoryEntry> MemoryEntries { get; set; } = null!;
        public DbSet<EventStoreEntry> EventStoreEntries { get; set; } = null!;
        public DbSet<JobLog> JobLogs { get; set; } = null!;
        public DbSet<ProjectRepository> ProjectRepositories { get; set; } = null!;
        public DbSet<MilestonePayment> MilestonePayments { get; set; } = null!;
        public DbSet<SupportTicket> SupportTickets { get; set; } = null!;
        public DbSet<ChatMessage> ChatMessages { get; set; } = null!;
        public DbSet<LearningPath> LearningPaths { get; set; } = null!;
        public DbSet<PortfolioItem> PortfolioItems { get; set; } = null!;
        public DbSet<UserPortfolio> UserPortfolios { get; set; } = null!;
        public DbSet<LiveEvent> LiveEvents { get; set; } = null!;
        public DbSet<SubscriptionPlan> SubscriptionPlans { get; set; } = null!;
        public DbSet<UserSubscription> UserSubscriptions { get; set; } = null!;
        public DbSet<AgentDecisionLog> AgentDecisionLogs { get; set; } = null!;
        public DbSet<Dispute> Disputes { get; set; } = null!;

        public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<MemoryEntry>()
                .Property(e => e.Embedding)
                .HasColumnType("vector(1536)");
            modelBuilder.Entity<MemoryEntry>()
                .HasIndex(e => e.Embedding)
                .HasMethod("hnsw")
                .HasOperators("vector_cosine_ops")
                .HasStorageParameter("m", 16)
                .HasStorageParameter("ef_construction", 200);

            modelBuilder.Entity<ProjectRepository>().HasIndex(r => r.ProjectId).IsUnique();
            modelBuilder.Entity<MilestonePayment>().HasIndex(m => m.MilestoneId);
            modelBuilder.Entity<SupportTicket>().HasIndex(t => t.ProjectId);
            modelBuilder.Entity<ChatMessage>().HasIndex(c => c.ProjectId);
            modelBuilder.Entity<ClientJourney>().HasMany(j => j.Steps).WithOne().HasForeignKey(s => s.JourneyId);
            modelBuilder.Entity<ClientJourney>().HasIndex(j => new { j.TenantId, j.Stage });
            modelBuilder.Entity<JobPosting>().HasIndex(j => j.UrlHash).IsUnique();
            modelBuilder.Entity<LearningPath>().HasIndex(l => new { l.TenantId, l.UserId, l.Week });
            modelBuilder.Entity<UserPortfolio>().HasIndex(u => new { u.TenantId, u.UserId });
            modelBuilder.Entity<UserSubscription>().HasIndex(u => new { u.TenantId, u.UserId });
            modelBuilder.Entity<AgentDecisionLog>().HasIndex(l => new { l.AgentName, l.CreatedAt });
            modelBuilder.Entity<Project>()
                .HasOne(p => p.Client)
                .WithMany()
                .HasForeignKey(p => p.ClientId)
                .OnDelete(DeleteBehavior.Restrict);
        }
    }

    // Repository & Unit of Work (fixed)
    public interface IUnitOfWork { Task<int> SaveChangesAsync(CancellationToken cancellationToken = default); IDbContextTransaction BeginTransaction(); }
    public class UnitOfWork : IUnitOfWork
    {
        private readonly AppDbContext _context;
        public UnitOfWork(AppDbContext context) => _context = context;
        public async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) => await _context.SaveChangesAsync(cancellationToken);
        public IDbContextTransaction BeginTransaction() => _context.Database.BeginTransaction();
    }

    public interface IRepository<T> where T : class
    {
        Task<T> GetByIdAsync(int id);
        Task<IEnumerable<T>> GetAllAsync();
        Task AddAsync(T entity);
        Task UpdateAsync(T entity);
        void Delete(T entity);
        IQueryable<T> Query(bool ignoreTenantFilter = false);
        IQueryable<T> QueryNoTracking(bool ignoreTenantFilter = false);
    }

    public class Repository<T> : IRepository<T> where T : class
    {
        protected readonly AppDbContext _context;
        private readonly ITenantContext _tenantContext;

        public Repository(AppDbContext context, ITenantContext tenantContext)
        {
            _context = context;
            _tenantContext = tenantContext;
        }

        public IQueryable<T> Query(bool ignoreTenantFilter = false)
        {
            var query = _context.Set<T>().AsQueryable();
            if (ignoreTenantFilter) return query;

            var entityType = typeof(T);
            var tenantProp = entityType.GetProperty("TenantId");
            if (tenantProp == null) return query;

            var currentTenant = _tenantContext.CurrentTenantId;
            return query.Where(e => EF.Property<string>(e, "TenantId") == currentTenant);
        }

        public IQueryable<T> QueryNoTracking(bool ignoreTenantFilter = false) => Query(ignoreTenantFilter).AsNoTracking();

        public async Task<T> GetByIdAsync(int id)
            => await Query().FirstOrDefaultAsync(e => EF.Property<int>(e, "Id") == id);

        public async Task<IEnumerable<T>> GetAllAsync()
            => await Query().ToListAsync();

        public async Task AddAsync(T entity)
            => await _context.Set<T>().AddAsync(entity);

        public Task UpdateAsync(T entity)
        {
            _context.Entry(entity).State = EntityState.Modified;
            return Task.CompletedTask;
        }

        public void Delete(T entity)
            => _context.Set<T>().Remove(entity);
    }

    // RefreshTokenService
    public interface IRefreshTokenService { Task RevokeAsync(string tokenHash); Task<bool> IsRevokedAsync(string tokenHash); }
    public class RefreshTokenService : IRefreshTokenService
    {
        private readonly IConnectionMultiplexer _redis;
        private readonly ILogger<RefreshTokenService> _logger;
        public RefreshTokenService(IConnectionMultiplexer redis, ILogger<RefreshTokenService> logger) { _redis = redis; _logger = logger; }
        public async Task RevokeAsync(string tokenHash)
        {
            var db = _redis.GetDatabase();
            await db.StringSetAsync($"revoked_token:{tokenHash}", "1", TimeSpan.FromDays(7));
            _logger.LogInformation("Token hash revoked.");
        }
        public async Task<bool> IsRevokedAsync(string tokenHash)
        {
            var db = _redis.GetDatabase();
            return await db.KeyExistsAsync($"revoked_token:{tokenHash}");
        }
    }

    // Event Bus
    public interface IEventBus { Task PublishAsync<T>(T @event) where T : IDomainEvent; void Subscribe<T, THandler>() where T : IDomainEvent where THandler : IEventHandler<T>; }
    public interface IEventHandler<T> where T : IDomainEvent { Task HandleAsync(T @event); }

    public class RabbitMQEventBus : IEventBus, IDisposable, IAsyncDisposable
    {
        private readonly IConnection _connection;
        private readonly IModel _channel;
        private readonly ILogger<RabbitMQEventBus> _logger;
        private readonly IServiceProvider _serviceProvider;
        private readonly Config _config;
        private readonly AsyncPolicy _retryPolicy;
        private readonly CircuitBreakerPolicy _circuitBreaker;
        private bool _disposed;

        public RabbitMQEventBus(Config config, ILogger<RabbitMQEventBus> logger, IServiceProvider serviceProvider)
        {
            _logger = logger; _serviceProvider = serviceProvider; _config = config;
            _circuitBreaker = Policy.Handle<Exception>().CircuitBreaker(
                config.CircuitBreakerFailureThreshold,
                TimeSpan.FromSeconds(config.CircuitBreakerDurationSeconds));
            _retryPolicy = Policy.Handle<Exception>().WaitAndRetryAsync(
                config.RetryCount,
                retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)));

            _connection = _circuitBreaker.Execute(() =>
            {
                var factory = new ConnectionFactory
                {
                    HostName = config.RabbitMQHost,
                    UserName = config.RabbitMQUser,
                    Password = config.RabbitMQPassword,
                    DispatchConsumersAsync = true
                };
                return factory.CreateConnection();
            });
            _channel = _connection.CreateModel();
            _channel.ExchangeDeclare(config.RabbitMQDeadLetterExchange, ExchangeType.Topic, durable: true);
            _channel.ExchangeDeclare("bpo.events", ExchangeType.Topic, durable: true);
            _channel.BasicQos(0, config.RabbitMQPrefetchCount, false);
        }

        public async Task PublishAsync<T>(T @event) where T : IDomainEvent
        {
            await _retryPolicy.ExecuteAsync(async () =>
            {
                var eventName = @event.GetType().Name;
                var payload = JsonSerializer.Serialize(@event);
                var tenantId = (@event as ITenantEvent)?.TenantId ?? "system";
                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var entry = new EventStoreEntry
                {
                    EventType = eventName,
                    Payload = JsonDocument.Parse(payload),
                    TenantId = tenantId,
                    Processed = false,
                    CreatedAt = DateTime.UtcNow
                };
                db.EventStoreEntries.Add(entry);
                await db.SaveChangesAsync();

                var body = Encoding.UTF8.GetBytes(payload);
                var properties = _channel.CreateBasicProperties();
                properties.Persistent = true;
                properties.Headers = new Dictionary<string, object> { { "tenantId", tenantId } };

                _channel.BasicPublish("bpo.events", eventName, properties, body);
                _logger.LogInformation("Event {EventName} published (Id {Id})", eventName, entry.Id);
            });
        }

        public void Subscribe<T, THandler>() where T : IDomainEvent where THandler : IEventHandler<T>
        {
            var eventName = typeof(T).Name;
            var queueName = $"{eventName}.{typeof(THandler).Name}";
            _channel.QueueDeclare(queueName, durable: true, exclusive: false, autoDelete: false,
                arguments: new Dictionary<string, object> { { "x-dead-letter-exchange", _config.RabbitMQDeadLetterExchange } });
            _channel.QueueBind(queueName, "bpo.events", eventName);
            var consumer = new AsyncEventingBasicConsumer(_channel);
            consumer.Received += async (model, ea) =>
            {
                var body = ea.Body.ToArray();
                var message = Encoding.UTF8.GetString(body);
                var @event = JsonSerializer.Deserialize<T>(message);
                if (@event != null)
                {
                    try
                    {
                        using var scope = _serviceProvider.CreateScope();
                        var tenantContext = scope.ServiceProvider.GetRequiredService<ITenantContext>();
                        tenantContext.CurrentTenantId = (@event as ITenantEvent)?.TenantId ?? "system";
                        var handler = scope.ServiceProvider.GetRequiredService<THandler>();
                        await _retryPolicy.ExecuteAsync(() => handler.HandleAsync(@event));
                        _channel.BasicAck(ea.DeliveryTag, false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error processing event {EventName}", eventName);
                        _channel.BasicNack(ea.DeliveryTag, false, true);
                    }
                }
                else
                {
                    _channel.BasicAck(ea.DeliveryTag, false);
                }
            };
            _channel.BasicConsume(queueName, autoAck: false, consumer);
            _logger.LogInformation("Subscribed to {EventName}", eventName);
        }

        internal async Task DirectPublishAsync(string routingKey, byte[] body)
        {
            var properties = _channel.CreateBasicProperties();
            properties.Persistent = true;
            _channel.BasicPublish("bpo.events", routingKey, properties, body);
            await Task.CompletedTask;
        }

        public void Dispose() { if (!_disposed) { _channel?.Close(); _connection?.Close(); _disposed = true; } }
        public async ValueTask DisposeAsync() { if (!_disposed) { _channel?.Close(); await _connection.CloseAsync(); _disposed = true; } }
    }

    // Event Outbox Service
    public class EventOutboxService : BackgroundService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<EventOutboxService> _logger;
        private readonly TimeSpan _interval = TimeSpan.FromSeconds(10);
        public EventOutboxService(IServiceScopeFactory scopeFactory, ILogger<EventOutboxService> logger) { _scopeFactory = scopeFactory; _logger = logger; }
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    using var scope = _scopeFactory.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                    var bus = scope.ServiceProvider.GetRequiredService<RabbitMQEventBus>();
                    var tenantContext = scope.ServiceProvider.GetRequiredService<ITenantContext>();
                    var pending = await db.EventStoreEntries.Where(e => !e.Processed && e.Retries < 5).OrderBy(e => e.CreatedAt).Take(50).ToListAsync(stoppingToken);
                    foreach (var entry in pending)
                    {
                        try
                        {
                            tenantContext.CurrentTenantId = entry.TenantId;
                            var body = Encoding.UTF8.GetBytes(entry.Payload.RootElement.GetRawText());
                            await bus.DirectPublishAsync(entry.EventType, body);
                            entry.Processed = true;
                            entry.ProcessedAt = DateTime.UtcNow;
                        }
                        catch (Exception ex)
                        {
                            entry.Retries++;
                            _logger.LogWarning(ex, "Event {Id} retry {Retries}", entry.Id, entry.Retries);
                        }
                    }
                    await db.SaveChangesAsync(stoppingToken);
                }
                catch (Exception ex) { _logger.LogError(ex, "Outbox processor failed"); }
                await Task.Delay(_interval, stoppingToken);
            }
        }
    }

    // Logged Background Service Base
    public abstract class LoggedBackgroundService : BackgroundService
    {
        protected abstract string JobName { get; }
        protected readonly IServiceScopeFactory _scopeFactory;
        protected readonly ILogger _logger;
        protected LoggedBackgroundService(IServiceScopeFactory scopeFactory, ILogger logger) { _scopeFactory = scopeFactory; _logger = logger; }
        protected async Task LogJobAsync(string status, string? message = null)
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.JobLogs.Add(new JobLog { JobName = JobName, Status = status, Message = message, ExecutedAt = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }
    }
}
