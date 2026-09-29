namespace DayCap.Api.Tests.Golden;

/// <summary>
/// REQUIREMENTS.md §23.2～§23.5 黃金情境骨架。
/// 每個情境一個測試，先 Skip。對應功能做好後：你親手算預期值 → 填進測試 → 拿掉 Skip（§0.2）。
/// AI 只負責把「輸入」組出來，不填預期值。
/// </summary>
public class ScenarioGoldenTests
{
    private const string Pending = "功能未完成或預期值未填（§0.2）";

    // ---- §23.2 期間、邏輯日、發薪日 ----
    [Fact(Skip = Pending)] public void Period_payday_on_sunday() { }
    [Fact(Skip = Pending)] public void Period_payday_inside_lunar_new_year_holiday() { }
    [Fact(Skip = Pending)] public void Period_payday_after_a_makeup_workday() { }
    [Fact(Skip = Pending)] public void LogicalDay_saturday_0100_belongs_to_friday_weekday_budget() { }
    [Fact(Skip = Pending)] public void LogicalDay_payday_0200_belongs_to_previous_period() { }
    [Fact(Skip = Pending)] public void Period_with_long_holiday_fewer_weekdays_higher_unit_same_total() { }
    [Fact(Skip = Pending)] public void Period_february_28_and_29_days() { }
    [Fact(Skip = Pending)] public void Period_31_day_month() { }
    [Fact(Skip = Pending)] public void MidwayStart_start_date_is_last_day_of_period() { }
    [Fact(Skip = Pending)] public void MidwayStart_start_date_before_period_first_day() { }

    // ---- §23.3 設定版本 ----
    [Fact(Skip = Pending)] public void SettingsVersion_lunch_150_to_180_on_0915_keeps_0901_to_0914() { }
    [Fact(Skip = Pending)] public void SettingsVersion_same_day_change_scope() { }

    // ---- §23.4 超支、對帳、帳戶 ----
    [Fact(Skip = Pending)] public void Overspent_midmonth_then_leave_reported_next_day() { }
    [Fact(Skip = Pending)] public void Reconcile_minus_1200_then_report_1000_leaves_200() { }
    [Fact(Skip = Pending)] public void Overspend_on_last_day_nothing_to_spread() { }
    [Fact(Skip = Pending)] public void Leave_and_overspend_on_same_day() { }
    [Fact(Skip = Pending)] public void Rounding_100_over_3_days_sums_to_100() { }
    [Fact(Skip = Pending)] public void Overspend_hits_guardrail_floor() { }
    [Fact(Skip = Pending)] public void Reconcile_with_unpaid_credit_card() { }
    [Fact(Skip = Pending)] public void Reconcile_on_card_payment_day() { }
    [Fact(Skip = Pending)] public void Transfer_between_accounts_is_not_spending() { }
    [Fact(Skip = Pending)] public void Leave_personal_unpaid_sick_half_daily_rate_basis() { }
    [Fact(Skip = Pending)] public void Year_end_bonus_destination() { }

    // ---- §23.5 罐子、分期 ----
    [Fact(Skip = Pending)] public void AnnualReserve_12_contributions_fill_exactly() { }
    [Fact(Skip = Pending)] public void Installment_early_repayment_lower_payment() { }
    [Fact(Skip = Pending)] public void Installment_early_repayment_shorter_term() { }
    [Fact(Skip = Pending)] public void Installment_zero_interest_with_fee_total_cost() { }
    [Fact(Skip = Pending)] public void Reservation_due_actual_higher_than_reserved() { }
    [Fact(Skip = Pending)] public void Reservation_due_actual_lower_than_reserved() { }
}
