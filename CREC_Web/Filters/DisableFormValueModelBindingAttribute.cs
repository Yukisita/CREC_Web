using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace CREC_Web.Filters;

/// <summary>フォームをアクション内で読み取り、受信エラーを直接処理できるようにする。</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class DisableFormValueModelBindingAttribute : Attribute, IResourceFilter
{
    /// <summary>モデルバインド前にフォームを自動で読み取るプロバイダーを除外する。</summary>
    /// <param name="context">対象の要求と値プロバイダーを含む実行コンテキスト。</param>
    public void OnResourceExecuting(ResourceExecutingContext context)
    {
        context.ValueProviderFactories.RemoveType<FormValueProviderFactory>();
        context.ValueProviderFactories.RemoveType<FormFileValueProviderFactory>();
        context.ValueProviderFactories.RemoveType<JQueryFormValueProviderFactory>();
    }

    /// <summary>リソース実行後の追加処理は不要。</summary>
    /// <param name="context">リソースの実行結果。</param>
    public void OnResourceExecuted(ResourceExecutedContext context) { }
}
