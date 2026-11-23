using System.ComponentModel.DataAnnotations;

namespace Baihua.Data.Entities
{
    /// <summary>
    /// 服务器地址配置 - 用于移动端连接
    /// </summary>
    public class ServerAddressSetting
    {
        [Key]
        public int Id { get; set; }

        /// <summary>
        /// 域名（广域网场景）。设置后自动生成 https://域名
        /// 留空则使用局域网 HTTP 自动获取
        /// </summary>
        [MaxLength(500)]
        public string Domain { get; set; } = "";

        /// <summary>
        /// 完整服务器地址
        /// </summary>
        [MaxLength(500)]
        public string Url { get; set; } = "";

        /// <summary>
        /// 服务器显示名称（如"百花服务器"），用于移动端展示。留空则使用系统 hostname。
        /// </summary>
        [MaxLength(200)]
        public string DisplayName { get; set; } = "";

        /// <summary>
        /// 服务器实例唯一标识，首次运行时生成并持久化，用于移动端可靠区分不同服务器
        /// </summary>
        [MaxLength(100)]
        public string ServerInstanceId { get; set; } = "";

        /// <summary>
        /// 移动端 HMAC 签名共享密钥，首次运行时自动生成并持久化
        /// </summary>
        [MaxLength(100)]
        public string SharedSecret { get; set; } = "";

        /// <summary>
        /// 创建时间
        /// </summary>
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// 更新时间
        /// </summary>
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    }
}
