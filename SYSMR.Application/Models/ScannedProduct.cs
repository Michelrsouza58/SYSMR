using SYSMR.Domain.Entities;

namespace SYSMR.Application.Models;

public sealed record ScannedProduct(Product Product, decimal Quantity);
