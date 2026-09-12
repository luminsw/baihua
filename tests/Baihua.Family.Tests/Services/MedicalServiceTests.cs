using Baihua.Contracts.Medical;
using Baihua.Data;
using Baihua.Modules.Family.Services.Medical;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Baihua.Family.Tests.Services;

/// <summary>
/// 家庭病历本服务测试：成员档案 / 病历记录（症状/诊断/用药 JSON 往返）/ AI 诊断 / 级联删除。
/// 使用内存 SQLite（now() 由 TestSqliteDb 注册），不触碰真实数据库。
/// </summary>
public class MedicalServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly IDbContextFactory<FamilyDbContext> _factory;

    public MedicalServiceTests()
    {
        _connection = TestDoubles.TestSqliteDb.OpenInMemory();
        var options = new DbContextOptionsBuilder<FamilyDbContext>()
            .UseSqlite(_connection)
            .Options;
        using (var db = new FamilyDbContext(options))
        {
            db.Database.EnsureCreated();
        }
        _factory = new FixedOptionsFactory(options);
    }

    public void Dispose()
    {
        _connection.Dispose();
    }

    private MedicalService CreateService() => new(_factory, Mock.Of<ILogger<MedicalService>>());

    [Fact]
    public async Task CreateAndGetMember_JsonListsRoundTrip()
    {
        var service = CreateService();

        var member = await service.CreateMemberAsync(
            "小明", "男", new DateTime(2015, 5, 20), "B",
            new[] { "青霉素过敏", "花生过敏" }, new[] { "哮喘" }, "注意花粉季节",
            null, null, null, null, new List<string>(), null,
            CancellationToken.None);
        Assert.NotNull(member);

        var loaded = await service.GetMemberAsync(member!.Id, CancellationToken.None);
        Assert.NotNull(loaded);
        Assert.Equal("小明", loaded!.Name);
        Assert.Equal("男", loaded.Gender);
        // 出生日期在服务端转 UTC 存储，用 Ticks 比较（与机器时区无关，避免本地日期偏移）
        Assert.Equal(new DateTime(2015, 5, 20).ToUniversalTime().Ticks, loaded.BirthDate!.Value.Ticks);

        var allergies = MedicalService.DeserializeStringList(loaded.AllergiesJson);
        Assert.Equal(new[] { "青霉素过敏", "花生过敏" }, allergies);
        Assert.Equal(new[] { "哮喘" }, MedicalService.DeserializeStringList(loaded.ChronicDiseasesJson));
    }

    [Fact]
    public async Task CreateMember_EmptyName_ReturnsNull()
    {
        var service = CreateService();
        var member = await service.CreateMemberAsync("  ", "", null, "", new List<string>(), new List<string>(), null, null, null, null, null, new List<string>(), null, CancellationToken.None);
        Assert.Null(member);
    }

    [Fact]
    public async Task UpdateMember_ChangeNameAndClearAllergies()
    {
        var service = CreateService();
        var member = await service.CreateMemberAsync(
            "小红", "女", null, "A", new[] { "海鲜过敏" }, new List<string>(), null, null, null, null, null, new List<string>(), null, CancellationToken.None);
        Assert.NotNull(member);

        var updated = await service.UpdateMemberAsync(
            member!.Id, "小红红", null, null, null, new List<string>(), null, null, null, null, null, null, new List<string>(), null, CancellationToken.None);
        Assert.NotNull(updated);
        Assert.Equal("小红红", updated!.Name);
        Assert.Empty(MedicalService.DeserializeStringList(updated.AllergiesJson));
    }

    [Fact]
    public async Task CreateRecord_MedicationsRoundTrip()
    {
        var service = CreateService();
        var member = await service.CreateMemberAsync("爸爸", "男", null, "O", new List<string>(), new List<string>(), null, null, null, null, null, new List<string>(), null, CancellationToken.None);
        Assert.NotNull(member);

        var record = await service.CreateRecordAsync(
            member!.Id, new DateTime(2026, 8, 1), "感冒发烧",
            new[] { "发热 38.5℃", "流鼻涕" }, new[] { "上呼吸道感染" },
            new List<MedicalMedicationItemDto>
            {
                new() { Name = "布洛芬", Dosage = "每次 1 片", Frequency = "每日 3 次", Note = "饭后服用" },
                new() { Name = "", Dosage = "x", Frequency = "y", Note = "z" } // 空药名应被过滤
            },
            "市一医院 内科 张医生", null, CancellationToken.None);
        Assert.NotNull(record);

        var loaded = await service.GetRecordAsync(record!.Id, CancellationToken.None);
        Assert.NotNull(loaded);
        Assert.Equal(new[] { "发热 38.5℃", "流鼻涕" }, MedicalService.DeserializeStringList(loaded!.SymptomsJson));
        Assert.Equal(new[] { "上呼吸道感染" }, MedicalService.DeserializeStringList(loaded.DiagnosesJson));

        var meds = MedicalService.DeserializeMedications(loaded.MedicationsJson);
        Assert.Single(meds);
        Assert.Equal("布洛芬", meds[0].Name);
        Assert.Equal("每次 1 片", meds[0].Dosage);
        Assert.Equal("每日 3 次", meds[0].Frequency);
        Assert.Equal("饭后服用", meds[0].Note);

        var records = await service.GetRecordsAsync(member.Id, CancellationToken.None);
        Assert.Single(records);
    }

    [Fact]
    public async Task DeleteMember_CascadesRecordsAndDiagnoses()
    {
        var service = CreateService();
        var member = await service.CreateMemberAsync("奶奶", "女", null, "", new List<string>(), new[] { "高血压" }, null, null, null, null, null, new List<string>(), null, CancellationToken.None);
        Assert.NotNull(member);

        await service.CreateRecordAsync(member!.Id, DateTime.Now, "头晕", new[] { "头晕" }, new List<string>(), new List<MedicalMedicationItemDto>(), null, null, CancellationToken.None);
        await service.SaveDiagnosisAsync(member.Id, "最近头晕", "**可能原因**：血压波动。", ct: CancellationToken.None);

        Assert.True(await service.DeleteMemberAsync(member.Id, CancellationToken.None));
        Assert.Null(await service.GetMemberAsync(member.Id, CancellationToken.None));
        Assert.Empty(await service.GetRecordsAsync(member.Id, CancellationToken.None));
        Assert.Empty(await service.GetDiagnosesAsync(member.Id, CancellationToken.None));
    }

    [Fact]
    public async Task SaveDiagnosis_ThenDelete()
    {
        var service = CreateService();
        var member = await service.CreateMemberAsync("妈妈", "女", null, "", new List<string>(), new List<string>(), null, null, null, null, null, new List<string>(), null, CancellationToken.None);
        Assert.NotNull(member);

        var diagnosis = await service.SaveDiagnosisAsync(member!.Id, "咳嗽三天", "**仅供参考**", ct: CancellationToken.None);
        var list = await service.GetDiagnosesAsync(member.Id, CancellationToken.None);
        Assert.Single(list);
        Assert.Equal("咳嗽三天", list[0].SymptomText);

        Assert.True(await service.DeleteDiagnosisAsync(diagnosis.Id, CancellationToken.None));
        Assert.Empty(await service.GetDiagnosesAsync(member.Id, CancellationToken.None));
        Assert.False(await service.DeleteDiagnosisAsync(diagnosis.Id, CancellationToken.None));
    }

    [Fact]
    public async Task SearchRecords_ByKeyword()
    {
        var service = CreateService();
        var member = await service.CreateMemberAsync("小明", "男", null, "", new List<string>(), new List<string>(), null, null, null, null, null, new List<string>(), null, CancellationToken.None);
        Assert.NotNull(member);

        await service.CreateRecordAsync(member!.Id, new DateTime(2026, 8, 2), "偏头痛发作", new[] { "右侧头痛" }, new List<string>(), new List<MedicalMedicationItemDto>(), null, null, CancellationToken.None);
        await service.CreateRecordAsync(member.Id, new DateTime(2026, 8, 1), "感冒", new[] { "流鼻涕" }, new List<string>(), new List<MedicalMedicationItemDto>(), null, null, CancellationToken.None);

        var hits = await service.SearchRecordsAsync("头痛", 50, CancellationToken.None);
        Assert.Single(hits);
        Assert.Equal("偏头痛发作", hits[0].Title);

        var none = await service.SearchRecordsAsync("不存在关键词", 50, CancellationToken.None);
        Assert.Empty(none);
    }

    [Fact]
    public async Task CreateMember_NewFieldsRoundTrip()
    {
        var service = CreateService();
        var constitution = new ConstitutionDto { Primary = "气虚", Secondary = "痰湿", Note = "易疲劳" };
        var member = await service.CreateMemberAsync(
            "小华", "男", null, "", new List<string>(), new List<string>(), null,
            175.5, 68.2, "工程师", "熬夜较多", new[] { "踝关节扭伤" }, constitution, CancellationToken.None);
        Assert.NotNull(member);

        var loaded = await service.GetMemberAsync(member!.Id, CancellationToken.None);
        Assert.NotNull(loaded);
        Assert.Equal(175.5, loaded!.HeightCm);
        Assert.Equal(68.2, loaded.WeightKg);
        Assert.Equal("工程师", loaded.Occupation);
        Assert.Equal("熬夜较多", loaded.LifeHabits);
        Assert.Equal(new[] { "踝关节扭伤" }, MedicalService.DeserializeStringList(loaded.SportsInjuriesJson));

        var c = MedicalService.DeserializeConstitution(loaded.ConstitutionJson);
        Assert.NotNull(c);
        Assert.Equal("气虚", c!.Primary);
        Assert.Equal("痰湿", c.Secondary);
    }

    [Fact]
    public async Task CreateRecord_FourDiagnosticsAndIngredientsRoundTrip()
    {
        var service = CreateService();
        var member = await service.CreateMemberAsync("妈妈", "女", null, "", new List<string>(), new List<string>(), null, null, null, null, null, new List<string>(), null, CancellationToken.None);
        Assert.NotNull(member);

        var fd = new FourDiagnosticsDto
        {
            Tongue = new TongueDto { Color = "淡红", CoatingThickness = "薄" },
            Pulse = new PulseDto { Quality = "弦" },
            ColdHeat = "畏寒",
            Sleep = "多梦"
        };
        var meds = new List<MedicalMedicationItemDto>
        {
            new()
            {
                Name = "桂枝汤",
                Ingredients = new List<IngredientDto>
                {
                    new() { Name = "桂枝", Dosage = "9g" },
                    new() { Name = "白芍", Dosage = "9g" }
                },
                DecoctionMethod = "水煎分 2 次温服",
                Principle = "调和营卫",
                Course = "5 天",
                Effect = "解肌发表"
            }
        };

        var record = await service.CreateRecordAsync(
            member!.Id, new DateTime(2026, 8, 3), "太阳中风",
            new[] { "汗出恶风" }, new[] { "太阳中风证" }, meds, null, fd, CancellationToken.None);
        Assert.NotNull(record);

        var loaded = await service.GetRecordAsync(record!.Id, CancellationToken.None);
        Assert.NotNull(loaded);

        var four = MedicalService.DeserializeFourDiagnostics(loaded!.FourDiagnosticsJson);
        Assert.NotNull(four);
        Assert.Equal("淡红", four!.Tongue!.Color);
        Assert.Equal("弦", four.Pulse!.Quality);
        Assert.Equal("畏寒", four.ColdHeat);

        var meds2 = MedicalService.DeserializeMedications(loaded.MedicationsJson);
        Assert.Single(meds2);
        Assert.Equal("桂枝汤", meds2[0].Name);
        Assert.Equal("调和营卫", meds2[0].Principle);
        Assert.NotNull(meds2[0].Ingredients);
        Assert.Equal(2, meds2[0].Ingredients!.Count);
        Assert.Equal("桂枝", meds2[0].Ingredients![0].Name);
        Assert.Equal("9g", meds2[0].Ingredients![0].Dosage);
    }

    [Fact]
    public void DetectRedFlags_ReturnsWarning()
    {
        var warn = MedicalAiService.DetectRedFlags("高热不退三天，呼吸困难");
        Assert.NotNull(warn);
        Assert.Contains("高热", warn);

        Assert.Null(MedicalAiService.DetectRedFlags("普通感冒，流鼻涕"));
        Assert.Null(MedicalAiService.DetectRedFlags(""));
    }

    private sealed class FixedOptionsFactory(DbContextOptions<FamilyDbContext> options) : IDbContextFactory<FamilyDbContext>
    {
        public FamilyDbContext CreateDbContext() => new(options);
        public Task<FamilyDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(CreateDbContext());
    }
}
