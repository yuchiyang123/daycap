namespace DayCap.Api.Services.Onboarding;

/// <summary>範本裡的一個大區塊（§20.4）。Percent 是佔月收入的 %。</summary>
public record TemplateBlock(string Key, string Name, decimal Percent, bool DefaultChecked = true, string? Hint = null);

public record MealWeights(decimal Breakfast, decimal Lunch, decimal Dinner, decimal HolidayMultiplier);

/// <summary>
/// 起點範本（§20.3–20.4）。三個問題（住家裡 / 租屋、外食 / 自己煮、雙北 / 其他）組出 8 個。
/// 數字怎麼來的寫在 docs/DESIGN_NOTES.md「範本 % 的推導」；改數字時版本號要加一（已套用的人不受影響，§20.6）。
/// </summary>
public record Template(string Code, int Version, string Name, List<TemplateBlock> Blocks, MealWeights Meals, List<string> FixedItemNames)
{
    public const string Rent = "rent";
    public const string Family = "family";
    public const string Food = "food";
    public const string Transport = "transport";
    public const string Insurance = "insurance";
    public const string Leisure = "leisure";
    public const string Savings = "savings";
}

public static class Templates
{
    private const string InsuranceHint = "年繳保費請用預付提撥（每月先存一份），不是取消勾選。";
    private const string FamilyHint = "給家裡的孝親費；沒有就取消勾選，這份會轉進儲蓄。";

    private static readonly MealWeights EatOut = new(1m, 1.5m, 1.8m, 1.2m);
    private static readonly MealWeights Cook = new(1m, 1.5m, 1.2m, 1m);

    public static readonly IReadOnlyList<Template> All =
    [
        Build("rent-out-tpe", "雙北租屋・大多外食", rent: 30, food: 25, transport: 6, leisure: 12, meals: EatOut),
        Build("rent-cook-tpe", "雙北租屋・大多自己煮", rent: 30, food: 18, transport: 6, leisure: 14, meals: Cook),
        Build("rent-out-other", "外縣市租屋・大多外食", rent: 22, food: 25, transport: 8, leisure: 13, meals: EatOut),
        Build("rent-cook-other", "外縣市租屋・大多自己煮", rent: 22, food: 18, transport: 8, leisure: 15, meals: Cook),
        Build("home-out-tpe", "雙北住家裡・大多外食", family: 10, food: 25, transport: 6, leisure: 17, meals: EatOut),
        Build("home-cook-tpe", "雙北住家裡・大多自己煮", family: 10, food: 15, transport: 6, leisure: 19, meals: Cook),
        Build("home-out-other", "外縣市住家裡・大多外食", family: 10, food: 25, transport: 8, leisure: 15, meals: EatOut),
        Build("home-cook-other", "外縣市住家裡・大多自己煮", family: 10, food: 15, transport: 8, leisure: 17, meals: Cook),
    ];

    /// <summary>保險 5%，儲蓄＝剩下的（讓每個範本加起來剛好 100%）。</summary>
    private static Template Build(string code, string name, decimal food, decimal transport, decimal leisure, MealWeights meals,
        decimal rent = 0, decimal family = 0)
    {
        const decimal insurance = 5;
        var savings = 100 - rent - family - food - transport - insurance - leisure;
        var blocks = new List<TemplateBlock>();
        if (rent > 0) blocks.Add(new(Template.Rent, "房租", rent));
        if (family > 0) blocks.Add(new(Template.Family, "孝親費", family, Hint: FamilyHint));
        blocks.Add(new(Template.Food, "餐費", food));
        blocks.Add(new(Template.Transport, "交通", transport));
        blocks.Add(new(Template.Insurance, "保險", insurance, Hint: InsuranceHint));
        blocks.Add(new(Template.Leisure, "娛樂", leisure));
        blocks.Add(new(Template.Savings, "儲蓄", savings));
        List<string> fixedNames = rent > 0 ? ["房租", "電信", "訂閱"] : ["電信", "訂閱"];
        return new Template(code, 1, name, blocks, meals, fixedNames);
    }

    public static Template? Find(string code) => All.FirstOrDefault(t => t.Code == code);

    public static string CodeFor(bool rents, bool eatsOut, bool taipei) =>
        $"{(rents ? "rent" : "home")}-{(eatsOut ? "out" : "cook")}-{(taipei ? "tpe" : "other")}";
}
