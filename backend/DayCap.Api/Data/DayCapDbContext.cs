using DayCap.Api.Models.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace DayCap.Api.Data;

public class DayCapDbContext(DbContextOptions<DayCapDbContext> options) : DbContext(options)
{
    public DbSet<UserProfile> Profiles => Set<UserProfile>();
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<DailySlot> DailySlots => Set<DailySlot>();
    public DbSet<FixedItem> FixedItems => Set<FixedItem>();
    public DbSet<DayTypeOverride> DayTypeOverrides => Set<DayTypeOverride>();
    public DbSet<CalendarDay> CalendarDays => Set<CalendarDay>();
    public DbSet<SettingsVersion> SettingsVersions => Set<SettingsVersion>();

    public DbSet<BudgetPeriod> Periods => Set<BudgetPeriod>();
    public DbSet<Entry> Entries => Set<Entry>();
    public DbSet<PoolTransfer> PoolTransfers => Set<PoolTransfer>();
    public DbSet<IncomeAdjustment> IncomeAdjustments => Set<IncomeAdjustment>();
    public DbSet<AssetAdjustment> AssetAdjustments => Set<AssetAdjustment>();
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<Reconciliation> Reconciliations => Set<Reconciliation>();
    public DbSet<ReconciliationLine> ReconciliationLines => Set<ReconciliationLine>();
    public DbSet<AccountTransfer> AccountTransfers => Set<AccountTransfer>();
    public DbSet<PeriodCarryover> PeriodCarryovers => Set<PeriodCarryover>();
    public DbSet<MonthEnd> MonthEnds => Set<MonthEnd>();

    public DbSet<CashAccount> CashAccounts => Set<CashAccount>();
    public DbSet<Holding> Holdings => Set<Holding>();
    public DbSet<Goal> Goals => Set<Goal>();
    public DbSet<AssetSnapshot> AssetSnapshots => Set<AssetSnapshot>();
    public DbSet<PriceQuote> PriceQuotes => Set<PriceQuote>();
    public DbSet<PushEndpoint> PushEndpoints => Set<PushEndpoint>();
    public DbSet<AppSecret> AppSecrets => Set<AppSecret>();
    public DbSet<Jar> Jars => Set<Jar>();
    public DbSet<Debt> Debts => Set<Debt>();
    public DbSet<DebtSettlement> DebtSettlements => Set<DebtSettlement>();
    public DbSet<Installment> Installments => Set<Installment>();
    public DbSet<InstallmentPrepayment> InstallmentPrepayments => Set<InstallmentPrepayment>();

    protected override void ConfigureConventions(ModelConfigurationBuilder c)
    {
        // 金額一律 decimal（§0.6）；沒特別指定的都用 18,2
        c.Properties<decimal>().HavePrecision(18, 2);
    }

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<UserProfile>().HasKey(x => x.UserId);

        b.Entity<Category>(e =>
        {
            e.HasIndex(x => x.UserId);
            e.Property(x => x.Name).HasMaxLength(40);
            e.Property(x => x.Percent).HasPrecision(6, 2);
            e.HasMany(x => x.Slots).WithOne().HasForeignKey(x => x.CategoryId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.FixedItems).WithOne().HasForeignKey(x => x.CategoryId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<DailySlot>().Property(x => x.Name).HasMaxLength(40);
        b.Entity<FixedItem>().Property(x => x.Name).HasMaxLength(60);

        b.Entity<SettingsVersion>(e =>
        {
            e.HasIndex(x => new { x.UserId, x.EffectiveFrom });
            e.Property(x => x.Note).HasMaxLength(200);
        });

        b.Entity<DayTypeOverride>().HasKey(x => new { x.UserId, x.Date });
        b.Entity<CalendarDay>().HasKey(x => x.Date);

        b.Entity<BudgetPeriod>(e =>
        {
            e.HasIndex(x => new { x.UserId, x.StartDate }).IsUnique();
            e.HasMany(x => x.Entries).WithOne().HasForeignKey(x => x.PeriodId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.PoolTransfers).WithOne().HasForeignKey(x => x.PeriodId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.IncomeAdjustments).WithOne().HasForeignKey(x => x.PeriodId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<IncomeAdjustment>(e =>
        {
            e.Property(x => x.Days).HasPrecision(6, 2);
            e.Property(x => x.Hours).HasPrecision(6, 2);
            e.Property(x => x.Note).HasMaxLength(120);
        });
        b.Entity<AssetAdjustment>(e =>
        {
            e.HasIndex(x => new { x.UserId, x.Date });
            e.Property(x => x.Note).HasMaxLength(120);
            e.Property(x => x.Source).HasMaxLength(20);
        });
        b.Entity<Notification>(e =>
        {
            e.HasIndex(x => new { x.UserId, x.Key }).IsUnique();
            e.Property(x => x.Key).HasMaxLength(80);
            e.Property(x => x.Kind).HasMaxLength(40);
        });
        b.Entity<Entry>(e =>
        {
            e.HasIndex(x => new { x.PeriodId, x.Date });
            e.Property(x => x.Note).HasMaxLength(120);
        });
        b.Entity<PoolTransfer>().Property(x => x.Note).HasMaxLength(120);

        b.Entity<CashAccount>().HasIndex(x => x.UserId);
        b.Entity<PeriodCarryover>(e =>
        {
            e.HasIndex(x => new { x.UserId, x.TargetDate });
            e.Property(x => x.Label).HasMaxLength(120);
        });
        b.Entity<MonthEnd>().HasIndex(x => new { x.UserId, x.PeriodId }).IsUnique();
        b.Entity<Reconciliation>(e =>
        {
            e.HasIndex(x => new { x.UserId, x.Date });
            e.Property(x => x.Note).HasMaxLength(120);
            e.HasMany(x => x.Lines).WithOne().HasForeignKey(x => x.ReconciliationId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<AccountTransfer>(e =>
        {
            e.HasIndex(x => new { x.UserId, x.Date });
            e.Property(x => x.Note).HasMaxLength(120);
        });
        b.Entity<Holding>(e =>
        {
            e.HasIndex(x => x.UserId);
            e.Property(x => x.Symbol).HasMaxLength(16);
            e.Property(x => x.Shares).HasPrecision(18, 4);
            e.Property(x => x.AvgCost).HasPrecision(18, 4);
            e.Property(x => x.ManualPrice).HasPrecision(18, 4);
        });
        b.Entity<Goal>().HasIndex(x => x.UserId);
        b.Entity<PushEndpoint>(e =>
        {
            e.HasIndex(x => x.UserId);
            e.HasIndex(x => x.Endpoint).IsUnique();
        });
        b.Entity<AppSecret>().HasKey(x => x.Key);
        b.Entity<Jar>().HasIndex(x => x.UserId);
        b.Entity<Debt>().HasIndex(x => new { x.UserId, x.Date });
        b.Entity<DebtSettlement>().HasIndex(x => new { x.UserId, x.DebtId });
        b.Entity<Installment>().HasIndex(x => x.UserId);
        b.Entity<InstallmentPrepayment>().HasIndex(x => new { x.UserId, x.InstallmentId });
        b.Entity<AssetSnapshot>().HasIndex(x => new { x.UserId, x.Date }).IsUnique();
        b.Entity<PriceQuote>(e =>
        {
            e.HasKey(x => x.Symbol);
            e.Property(x => x.Price).HasPrecision(18, 4);
        });

        // SQLite 不存時區，讀回來的 DateTime 是 Unspecified，序列化成 JSON 就沒有 Z，
        // 前端會當成本地時間。這裡寫入的一律是 UTC，讀回來時標回 UTC。
        var utc = new ValueConverter<DateTime, DateTime>(v => v, v => DateTime.SpecifyKind(v, DateTimeKind.Utc));
        var utcNullable = new ValueConverter<DateTime?, DateTime?>(v => v, v => v.HasValue ? DateTime.SpecifyKind(v.Value, DateTimeKind.Utc) : v);
        foreach (var entity in b.Model.GetEntityTypes())
        {
            foreach (var prop in entity.GetProperties())
            {
                if (prop.ClrType == typeof(DateTime)) prop.SetValueConverter(utc);
                else if (prop.ClrType == typeof(DateTime?)) prop.SetValueConverter(utcNullable);
            }
        }
    }
}
