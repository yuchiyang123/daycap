using DayCap.Api.Models.Entities;

namespace DayCap.Api.Services;

public record InstallmentRow(int Index, DateOnly DueDate, decimal Payment, decimal Principal, decimal Interest, decimal Fee, decimal BalanceAfter);

/// <summary>
/// 分期繳款表（§15），全部用 decimal、每期金額取整數元。
/// - 簡易：每期 MonthlyAmount 共 Periods 期，當成 0 利率、本金＝總額。
/// - 細部：本息平均攤還，月利率＝年利率 ÷ 12；0 利率＝本金 ÷ 期數。手續費總額平均加在每一期（零頭加在第一期）。
/// - 零頭：每期先取整，最後一期把本金剩下的全部還完。
/// - 提前還款：在那天把本金減掉；之後的期數依選擇「每期金額變少（期數不變）」或「期數變短（金額不變）」重算。
///   期數變短時，原本還沒繳的手續費加在新的最後一期（保守：手續費還是要付）。
/// </summary>
public static class InstallmentSchedule
{
    public static List<InstallmentRow> Build(Installment inst, IEnumerable<InstallmentPrepayment> prepayments)
    {
        var n = Math.Max(1, inst.Periods);
        var principal = inst.Mode == InstallmentMode.Simple ? (inst.MonthlyAmount ?? 0) * n : inst.Principal ?? 0;
        var rate = inst.Mode == InstallmentMode.Simple ? 0m : (inst.AnnualRatePercent ?? 0) / 1200m;
        var feeTotal = inst.Mode == InstallmentMode.Simple ? 0m : Math.Round(inst.Fee ?? 0, 0);
        var feeEach = Math.Floor(feeTotal / n);
        var feeFirstExtra = feeTotal - feeEach * n;

        var pending = prepayments.OrderBy(p => p.Date).ThenBy(p => p.CreatedAt).ToList();
        var rows = new List<InstallmentRow>();
        var balance = principal;
        var remainingCount = n;
        var payment = inst.Mode == InstallmentMode.Simple ? inst.MonthlyAmount ?? 0 : Payment(balance, rate, remainingCount);
        var feesLeft = feeTotal;

        for (var k = 0; balance > 0 && k < 600; k++)
        {
            var due = inst.FirstDueDate.AddMonths(k);

            // 這期繳款日之前的提前還款
            while (pending.Count > 0 && pending[0].Date < due && balance > 0)
            {
                var p = pending[0];
                pending.RemoveAt(0);
                balance = Math.Max(0, balance - p.Amount);
                if (balance > 0 && p.Mode == PrepayMode.ReduceAmount)
                    payment = Payment(balance, rate, remainingCount);
                // ReduceTerm：金額不變，下面自然提早還完
            }
            if (balance == 0) break;

            var interest = Math.Round(balance * rate, 0, MidpointRounding.AwayFromZero);
            var principalPart = Math.Min(balance, Math.Max(0, payment - interest));
            if (remainingCount <= 1) principalPart = balance;
            balance -= principalPart;

            var fee = Math.Min(feesLeft, feeEach + (k == 0 ? feeFirstExtra : 0));
            if (balance == 0) fee = feesLeft; // 期數變短：剩下的手續費加在最後一期
            feesLeft -= fee;

            rows.Add(new InstallmentRow(k + 1, due, principalPart + interest + fee, principalPart, interest, fee, balance));
            remainingCount = Math.Max(1, remainingCount - 1);
        }
        return rows;
    }

    /// <summary>本息平均攤還每期金額（取整數元）；0 利率＝本金 ÷ 期數。</summary>
    public static decimal Payment(decimal balance, decimal monthlyRate, int count)
    {
        if (count <= 0) return balance;
        if (monthlyRate == 0) return Math.Round(balance / count, 0, MidpointRounding.AwayFromZero);
        var factor = 1m;
        for (var i = 0; i < count; i++) factor *= 1 + monthlyRate;
        return Math.Round(balance * monthlyRate * factor / (factor - 1), 0, MidpointRounding.AwayFromZero);
    }
}
