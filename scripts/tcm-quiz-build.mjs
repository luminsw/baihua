#!/usr/bin/env node
/**
 * 中医·经方题库包构建脚本：node scripts/tcm-quiz-build.mjs
 *
 * 数据源：qihuang-journey 仓库的师承医案结构化数据（data/*.json + data/cases/*.json）
 * 产物：  baihua 仓库 data/tcm-quiz/jingfang-package.json（题库包，QuizBankPackage 契约格式）
 *
 * 转换规则（内容零生成——全部取自已批改的师承数据）：
 * - items：formulas.json 全量方剂 → 知识条目（条文/组成/煎服法/治法/禁忌，以药方为核心）
 * - questions：每案 2 题——①症状→选方（干扰项=该案批改时的干扰方+差异眼目）②症状→选证型
 *   （干扰项=干扰方的主治证型——结构性干扰，区分点同样适用）
 * - 解析=师父讲解全文；区分点=批改眼目（相似方/证型的关键鉴别）
 */

import { readFileSync, writeFileSync, mkdirSync } from 'node:fs'
import { join } from 'node:path'

const SRC = 'C:\\Users\\lumin\\src\\qihuang-journey\\data'
const OUT_DIR = join(process.cwd(), 'data', 'tcm-quiz')
const OUT = join(OUT_DIR, 'jingfang-package.json')

const read = (p) => JSON.parse(readFileSync(join(SRC, p), 'utf8'))
const formulas = read('formulas.json')
const patterns = read('patterns.json')
const herbs = read('herbs.json')
const symptoms = read('symptoms.json')
const treatments = read('treatments.json')


// id → 名称映射
const herbName = new Map(herbs.map((h) => [h.id, h.名称]))
const symptomName = new Map(symptoms.map((s) => [s.id, s.名称]))
const patternById = new Map(patterns.map((p) => [p.id, p]))
const treatmentName = new Map(treatments.map((t) => [t.id, t.名称]))
const formulaById = new Map(formulas.map((f) => [f.id, f]))

// 逐案读 cases 目录（cases.json 不存在）
import { readdirSync, existsSync } from 'node:fs'
const caseFiles = readdirSync(join(SRC, 'cases'))
  .filter((f) => f.endsWith('.json') && !f.endsWith('.draft.json'))
  .sort()
const caseList = caseFiles.map((f) => JSON.parse(readFileSync(join(SRC, 'cases', f), 'utf8')))

// 篇章 key（出处→篇）：『《金匮要略·黄疸病脉证并治》：…』→ 金匮·黄疸病；『《伤寒论》236条』→ 伤寒论
function chapterKey(source) {
  if (!source) return ''
  const m = /《(金匮要略|伤寒论)·([^：:》]+)》/.exec(source)
  if (m) return `${m[1] === '金匮要略' ? '金匮' : '伤寒'}·${m[2]}`
  if (source.includes('伤寒论')) return '伤寒'
  if (source.includes('金匮')) return '金匮'
  return ''
}
const formulaChapter = new Map(formulas.map((f) => [f.id, chapterKey(f.出处)]))
const patternChapter = new Map(patterns.map((p) => [p.id, chapterKey(p.出处)]))

// ── items：以药方为核心的知识条目 ──
const items = formulas.map((f) => {
  const indications = (f.主治证型 || []).map((pid) => patternById.get(pid)?.名称).filter(Boolean)
  const firstPattern = f.主治证型?.[0] ? patternById.get(f.主治证型[0]) : null
  const treatmentNames = (firstPattern?.治法 || []).map((t) => treatmentName.get(t)).filter(Boolean)
  const discriminations = (f.鉴别对象 || [])
    .map((fid) => formulaById.get(fid)?.名称)
    .filter(Boolean)
    .map((name) => ({ versus: name, point: `与「${f.名称}」互为鉴别（区分点见关联题目）` }))

  return {
    itemCode: f.id,
    itemType: 'formula',
    name: f.名称,
    summary: indications.length ? `${indications.join('；')} 之主方` : (f.出处 || '').slice(0, 80),
    content: {
      source: f.出处 || null,
      composition: (f.组成 || []).map((c) => ({
        name: herbName.get(c.药材) || c.药材,
        ancientDose: c.古量 || null,
        modernDose: c.今量 || null,
        role: c.君臣佐使 || null,
      })),
      decoction: f.煎服法全文 || null,
      pathogenesis: firstPattern ? firstPattern.名称 : null,
      treatment: treatmentNames.length ? treatmentNames.join('，') : null,
      indications,
      contraindications: f.禁忌 || [],
      discriminations: discriminations.length ? discriminations : null,
      notes: [firstPattern?.备注, ...(f.煎服法要素?.length ? [`煎服法要素：${f.煎服法要素.length} 条`] : [])]
        .filter(Boolean)
        .join('｜') || null,
    },
  }
})

// ── questions：每案 2 题（选方 + 选证型）──
const questions = []
const KEYS = ['A', 'B', 'C', 'D', 'E', 'F']

function buildOptions(correct, distractors, fillPool, fillCount) {
  // distractors: [{content, explanation}]；不足时从 fillPool（名称数组）随机补足
  const opts = []
  const used = new Set([correct.content])
  for (const d of distractors) {
    if (used.has(d.content)) continue
    used.add(d.content)
    opts.push({ content: d.content, explanation: d.explanation || null })
  }
  let pool = [...fillPool].sort(() => Math.random() - 0.5)
  for (const name of pool) {
    if (opts.length >= fillCount) break
    if (used.has(name)) continue
    used.add(name)
    opts.push({ content: name, explanation: null })
  }
  if (opts.length < fillCount) return null // 池子不够

  // 正确项随机插入
  const insertAt = Math.floor(Math.random() * (opts.length + 1))
  opts.splice(insertAt, 0, { content: correct.content, explanation: correct.explanation })

  return opts.slice(0, fillCount + 1 > opts.length ? opts.length : opts.length).map((o, i) => ({
    key: KEYS[i],
    content: o.content,
    isCorrect: o.content === correct.content,
    explanation: o.explanation,
  }))
}

for (const c of caseList) {
  const 案号 = (c.来源?.案号 || '').padStart(3, '0')
  if (!案号) continue

  const correctFormulaId = c.标准答案?.方剂
  const correctFormula = correctFormulaId ? formulaById.get(correctFormulaId) : null
  if (!correctFormula) continue

  const correctPatternId = c.标准答案?.证型
  const correctPattern = correctPatternId ? patternById.get(correctPatternId) : null

  // 结构化题干：患者概况 + 四诊要点
  const patientSentence = (c.患者 || '').split('。')[0] + '。'
  const signs = []
  for (const 诊 of ['望', '闻', '问', '切']) {
    for (const sid of c.四诊?.[诊] || []) {
      const name = symptomName.get(sid)
      if (name && !signs.includes(name)) signs.push(name)
    }
  }
  const stemDetail = { patient: patientSentence, signs }
  const stem = c.患者 || ''
  const analysis = c.讲解 || ''
  const sourceRef = (c.原文引用?.[0] || '').slice(0, 200) || null
  const difficulty = c.难度 || 3

  // 干扰方（来自批改时的干扰项——含差异眼目）
  const distractorFormulas = (c.干扰项 || [])
    .map((d) => {
      const f = formulaById.get(d.方剂)
      return f ? { formulaId: d.方剂, content: f.名称, explanation: d.差异眼目, point: d.差异眼目 } : null
    })
    .filter(Boolean)

  // 同篇章方池（补足干扰项）
  const chap = formulaChapter.get(correctFormulaId) || ''
  const sameChapterFormulas = formulas
    .filter((f) => f.id !== correctFormulaId && formulaChapter.get(f.id) === chap)
    .map((f) => f.名称)
    .filter((n) => n !== correctFormula.名称)

  // ── 题 1：症状 → 选方 ──
  const treatmentNames = (correctPattern?.治法 || []).map((t) => treatmentName.get(t)).filter(Boolean)
  const correctOption1 = {
    content: correctFormula.名称,
    explanation: correctPattern
      ? `证属「${correctPattern.名称}」${treatmentNames.length ? `，当${treatmentNames.join('、')}` : ''}，方证对应，故选本方。`
      : '方证对应，故选本方。',
  }
  const options1 = buildOptions(
    correctOption1,
    distractorFormulas.map((d) => ({ content: d.content, explanation: d.explanation })),
    sameChapterFormulas,
    3,
  )
  if (options1) {
    questions.push({
      questionCode: `q_${案号}_f`,
      itemCode: correctFormulaId,
      questionType: 'single_choice',
      target: 'formula',
      stem,
      stemDetail,
      options: options1,
      analysis,
      discrimination: distractorFormulas.map((d) => ({ versus: d.content, point: d.point })),
      difficulty,
      sourceRef,
    })
  }

  // ── 题 2：症状 → 选证型/病机（干扰=干扰方的主治证型：结构性干扰）──
  if (correctPattern) {
    const distractorPatterns = distractorFormulas
      .map((d) => {
        const df = formulaById.get(d.formulaId)
        const pid = df?.主治证型?.[0]
        const p = pid ? patternById.get(pid) : null
        return p && p.id !== correctPatternId
          ? { content: p.名称, explanation: d.explanation }
          : null
      })
      .filter(Boolean)

    const sameChapterPatterns = patterns
      .filter((p) => p.id !== correctPatternId && patternChapter.get(p.id) === patternChapter.get(correctPatternId))
      .map((p) => p.名称)

    const correctOption2 = {
      content: correctPattern.名称,
      explanation: `本案病机即「${correctPattern.名称}」${correctPattern.备注 ? `——${correctPattern.备注.slice(0, 100)}` : ''}。`,
    }
    const options2 = buildOptions(
      correctOption2,
      distractorPatterns,
      sameChapterPatterns,
      3,
    )
    if (options2) {
      questions.push({
        questionCode: `q_${案号}_p`,
        itemCode: correctFormulaId,
        questionType: 'single_choice',
        target: 'pattern',
        stem,
        stemDetail,
        options: options2,
        analysis,
        discrimination: distractorFormulas.map((d) => ({ versus: d.content, point: d.point })),
        difficulty,
        sourceRef,
      })
    }
  }
}

// ── 组装题库包 ──
const pkg = {
  formatVersion: 1,
  industry: { code: 'tcm', name: '中医' },
  bank: {
    code: 'jingfang',
    name: '经方',
    version: '2026.11.0',
    description: '伤寒论·金匮要略方剂辨证选择题库：症状→病机→选方，干扰项为相似方/相似证型，解析与区分点源自师承批改。',
  },
  items,
  questions,
}

mkdirSync(OUT_DIR, { recursive: true })
writeFileSync(OUT, JSON.stringify(pkg, null, 1) + '\n', 'utf8')

// 校验报告
const badQ = pkg.questions.filter((q) => q.options.filter((o) => o.isCorrect).length !== 1)
const shortQ = pkg.questions.filter((q) => q.options.length < 4)
console.log(`✓ 题库包已生成：${OUT}`)
console.log(`  items（方剂知识条目）：${pkg.items.length}`)
console.log(`  questions：${pkg.questions.length}（选方 ${pkg.questions.filter((q) => q.target === 'formula').length} + 选证型 ${pkg.questions.filter((q) => q.target === 'pattern').length}）`)
console.log(`  正确项≠1 的题：${badQ.length}（必须为 0）`)
console.log(`  选项<4 的题：${shortQ.length}`)
if (badQ.length > 0) {
  console.log('  异常题：' + badQ.map((q) => q.questionCode).join(', '))
  process.exit(1)
}