-- 迁移补丁：修正被早期错误 SQL 清空路径的那一行（NET开发智库），并取消软删
-- 背景：首次改写用了 LIKE 'C:\...'（PostgreSQL 的 LIKE 默认把反斜杠当转义符，匹配不到），
--       后改用 position()/substring() 才正确；本行是那次留下的残迹。
UPDATE "Vaults"
   SET "Path" = '/app/data/vaults/local/C#编程与.NET开发/NET开发智库',
       "IsDeleted" = false
 WHERE "Name" = 'NET开发智库'
   AND "Path" IN ('/app/data/vaults', '/vaults', 'C:/Users/lumin/.baihua/vaults');

\echo '--- 最终知识库状态 ---'
SELECT "Name", "Path", "IsDeleted" FROM "Vaults" ORDER BY "IsDeleted", "Name";
SELECT count(*) FILTER (WHERE "IsDeleted" = false) AS "可见", count(*) AS "总数" FROM "Vaults";
