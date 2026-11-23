using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Baihua.Contracts.Medical;
using Microsoft.Extensions.Logging;

namespace Baihua.Web.Services
{
    public partial class ApiService
    {
        // ===== 家庭病历本：成员档案 / 病历记录 / AI 诊断 =====

        public async Task<List<MedicalMemberDto>> GetMedicalMembersAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                using var quick = new CancellationTokenSource(QuickCallTimeout);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, quick.Token);
                var response = await GetWithMetricsAsync("/api/medical/members", linked.Token);
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadFromJsonAsync<List<MedicalMemberDto>>(linked.Token) ?? new List<MedicalMemberDto>();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取家庭成员档案失败");
                return new List<MedicalMemberDto>();
            }
        }

        public async Task<MedicalMemberDetailDto?> GetMedicalMemberDetailAsync(int id, CancellationToken cancellationToken = default)
        {
            try
            {
                using var quick = new CancellationTokenSource(QuickCallTimeout);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, quick.Token);
                var response = await GetWithMetricsAsync($"/api/medical/members/{id}", linked.Token);
                if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                    return null;
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadFromJsonAsync<MedicalMemberDetailDto>(linked.Token);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取家庭成员详情失败，Id={Id}", id);
                return null;
            }
        }

        public async Task<MedicalMemberDto> CreateMedicalMemberAsync(CreateMedicalMemberRequest request, CancellationToken cancellationToken = default)
        {
            try
            {
                using var quick = new CancellationTokenSource(QuickCallTimeout);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, quick.Token);
                var response = await PostWithMetricsAsync("/api/medical/members", JsonContent.Create(request), linked.Token);
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadFromJsonAsync<MedicalMemberDto>(linked.Token) ?? new MedicalMemberDto();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "创建家庭成员失败");
                throw;
            }
        }

        public async Task<MedicalMemberDto?> UpdateMedicalMemberAsync(int id, UpdateMedicalMemberRequest request, CancellationToken cancellationToken = default)
        {
            try
            {
                using var quick = new CancellationTokenSource(QuickCallTimeout);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, quick.Token);
                var json = JsonSerializer.Serialize(request);
                var content = new StringContent(json, Encoding.UTF8, "application/json");
                var response = await _httpClient.PutAsync($"/api/medical/members/{id}", content, linked.Token);
                if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                    return null;
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadFromJsonAsync<MedicalMemberDto>(linked.Token);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "更新家庭成员失败，Id={Id}", id);
                throw;
            }
        }

        public async Task<bool> DeleteMedicalMemberAsync(int id, CancellationToken cancellationToken = default)
        {
            try
            {
                using var quick = new CancellationTokenSource(QuickCallTimeout);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, quick.Token);
                var response = await DeleteWithMetricsAsync($"/api/medical/members/{id}");
                return response.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "删除家庭成员失败，Id={Id}", id);
                return false;
            }
        }

        public async Task<MedicalRecordDto> CreateMedicalRecordAsync(int memberId, CreateMedicalRecordRequest request, CancellationToken cancellationToken = default)
        {
            try
            {
                using var quick = new CancellationTokenSource(QuickCallTimeout);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, quick.Token);
                var response = await PostWithMetricsAsync($"/api/medical/members/{memberId}/records", JsonContent.Create(request), linked.Token);
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadFromJsonAsync<MedicalRecordDto>(linked.Token) ?? new MedicalRecordDto();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "创建病历记录失败，MemberId={MemberId}", memberId);
                throw;
            }
        }

        public async Task<MedicalRecordDto?> UpdateMedicalRecordAsync(int id, UpdateMedicalRecordRequest request, CancellationToken cancellationToken = default)
        {
            try
            {
                using var quick = new CancellationTokenSource(QuickCallTimeout);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, quick.Token);
                var json = JsonSerializer.Serialize(request);
                var content = new StringContent(json, Encoding.UTF8, "application/json");
                var response = await _httpClient.PutAsync($"/api/medical/records/{id}", content, linked.Token);
                if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                    return null;
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadFromJsonAsync<MedicalRecordDto>(linked.Token);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "更新病历记录失败，Id={Id}", id);
                throw;
            }
        }

        public async Task<bool> DeleteMedicalRecordAsync(int id, CancellationToken cancellationToken = default)
        {
            try
            {
                using var quick = new CancellationTokenSource(QuickCallTimeout);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, quick.Token);
                var response = await DeleteWithMetricsAsync($"/api/medical/records/{id}");
                return response.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "删除病历记录失败，Id={Id}", id);
                return false;
            }
        }

        /// <summary>AI 诊断（长耗时，走 FamilyApiLong 的 5 分钟超时）</summary>
        public async Task<AiDiagnoseResultDto> RunAiDiagnosisAsync(AiDiagnoseRequest request, CancellationToken cancellationToken = default)
        {
            try
            {
                using var quick = new CancellationTokenSource(TimeSpan.FromMinutes(3));
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, quick.Token);
                var response = await PostWithMetricsAsync("/api/medical/diagnose", JsonContent.Create(request), linked.Token, _longHttpClient);
                var body = await response.Content.ReadAsStringAsync(linked.Token);
                try
                {
                    var result = JsonSerializer.Deserialize<AiDiagnoseResultDto>(body, _caseInsensitiveJsonOptions);
                    if (result != null)
                    {
                        if (response.IsSuccessStatusCode)
                            return result;
                        return new AiDiagnoseResultDto { Success = false, Error = result.Error ?? _loc["Api_UnknownError"] };
                    }
                }
                catch (JsonException) { }

                return new AiDiagnoseResultDto { Success = false, Error = _loc["Api_ResponseParseFailed"] };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "AI 诊断请求失败，MemberId={MemberId}", request.MemberId);
                return new AiDiagnoseResultDto { Success = false, Error = _loc["Medical_DiagnoseFailed"] };
            }
        }

        public async Task<bool> DeleteAiDiagnosisAsync(int id, CancellationToken cancellationToken = default)
        {
            try
            {
                using var quick = new CancellationTokenSource(QuickCallTimeout);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, quick.Token);
                var response = await DeleteWithMetricsAsync($"/api/medical/diagnoses/{id}");
                return response.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "删除 AI 诊断记录失败，Id={Id}", id);
                return false;
            }
        }
    }
}
