namespace DayCap.Api.Common;

public class NotFoundException(string message) : Exception(message);

public class ValidationException(string message) : Exception(message);

/// <summary>還沒到設定的開始日期。轉成 409，前端顯示「還沒開始」。</summary>
public class NotStartedException(Models.Dtos.NotStartedDto info) : Exception("還沒到開始日期。")
{
    public Models.Dtos.NotStartedDto Info { get; } = info;
}
