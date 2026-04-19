## Phụ lục — Thứ tự phụ thuộc và kiểm tra

### Dependency graph

```
2A-1 Contracts
  └─► 2A-2 DeviceTemplate
        └─► 2A-3 ProtocolAdapter
              └─► 2A-4 PollingEngine
                    └─► 2A-5 EdgeRuleEngine
                          └─► 2A-6 QualityChecker
                                └─► 2A-7 SparkplugEncoder
                                      └─► 2A-8 LocalBuffer
                                            └─► 2A-9 AdminService
                                                  └─► 2A-10 CommandHandler (G3)
                                                        └─► 2A-11 Host
```

### Checklist trước khi chuyển module tiếp theo

| Bước | Kiểm tra |
|---|---|
| Sau 2A-1 | `dotnet build EMS.Gateway.Contracts` → 0 errors, 0 warnings |
| Sau 2A-2 | `dotnet test EMS.Gateway.DeviceTemplate.Tests` → all pass |
| Sau 2A-3 | `dotnet test EMS.Gateway.ProtocolAdapter.Tests` → all pass |
| Sau 2A-4 | `dotnet test EMS.Gateway.PollingEngine.Tests` → all pass |
| Sau 2A-5 | `dotnet test EMS.Gateway.EdgeRuleEngine.Tests` → all pass |
| Sau 2A-6 | `dotnet test EMS.Gateway.QualityChecker.Tests` → all pass |
| Sau 2A-7 | `dotnet test EMS.Gateway.SparkplugEncoder.Tests` → all pass |
| Sau 2A-8 | `dotnet test EMS.Gateway.LocalBuffer.Tests` → all pass |
| Sau 2A-9 | `dotnet build EMS.Gateway.AdminService` → 0 errors |
| Sau 2A-10 | `dotnet test EMS.Gateway.CommandHandler.Tests` → all pass |
| Sau 2A-11 | `dotnet test EMS.IIoTGateway.IntegrationTests` → all pass |

### NuGet packages summary

| Package | Version | Dùng trong |
|---|---|---|
| FluentModbus | 5.x | 2A-3 |
| MQTTnet | 4.3.x | 2A-3, 2A-7 |
| System.IO.BACnet | latest | 2A-3 |
| Polly | 8.x | 2A-3 |
| System.Threading.RateLimiting | built-in .NET 7+ | 2A-3, 2A-8 |
| NCalc2 | 2.x | 2A-2, 2A-5 |
| FluentValidation | 11.x | 2A-2 |
| Microsoft.Data.Sqlite | 8.x | 2A-8 |
| Eclipse.Tahu.Protobuf | latest | 2A-7 |
| Microsoft.IdentityModel.Tokens | 7.x | 2A-10 |
| System.IdentityModel.Tokens.Jwt | 7.x | 2A-10 |
| prometheus-net.AspNetCore | 8.x | 2A-9 |
| Serilog.Extensions.Hosting | 8.x | 2A-11 |
| Serilog.Sinks.Console + File | 5.x | 2A-11 |

---

*Phiên bản 1.0 — AI Coding Prompts cho toàn bộ 11 module Tầng 2A · Dựa trên EMS_IIoTGateway_Tier2A_Module_Decomposition_v1.8 · Mỗi prompt tự chứa đủ context · Thứ tự implement: 2A-1 → 2A-11*
