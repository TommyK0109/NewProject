using CSharp2.Application;
using CSharp2.Domain;

namespace CSharp2.Modules;

public static class PurchasingEndpoints
{
    public static IEndpointRouteBuilder MapPurchasingEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var orders = endpoints.MapGroup("/api/purchase-orders").RequireAuthorization();
        orders.MapGet("/", (PurchasingService service, Guid? departmentId, Guid? supplierId,
            PurchaseOrderStatus? status, string? search, int? page, int? pageSize) =>
            service.ListAsync(departmentId, supplierId, status, search, page, pageSize));
        orders.MapGet("/{id:guid}", (Guid id, PurchasingService service) => service.GetAsync(id));
        orders.MapPost("/", (PurchaseOrderCreate request, PurchasingService service) => service.CreateAsync(request));
        orders.MapPut("/{id:guid}", (Guid id, PurchaseOrderUpdate request, PurchasingService service) => service.UpdateAsync(id, request));
        orders.MapPost("/{id:guid}/approve", (Guid id, PurchaseOrderTransition request, PurchasingService service) => service.ApproveAsync(id, request));
        orders.MapPost("/{id:guid}/cancel", (Guid id, PurchaseOrderTransition request, PurchasingService service) => service.CancelAsync(id, request));
        return endpoints;
    }
}
