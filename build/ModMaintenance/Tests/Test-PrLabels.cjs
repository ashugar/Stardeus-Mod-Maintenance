// Exercise the actual inline workflow with a mocked GitHub API. No credentials/network.
const fs = require('node:fs');
const path = require('node:path');
const assert = require('node:assert/strict');
const yaml = fs.readFileSync(path.join(__dirname, '../../../.github/workflows/mod-labels.yml'), 'utf8');
const start = yaml.indexOf('          script: |\n');
assert.ok(start >= 0, 'Missing inline workflow script');
const script = yaml.slice(start + '          script: |\n'.length).split('\n').map(line => {
  if (line && !line.startsWith('            ')) throw new Error('Unexpected script indentation');
  return line.slice(12);
}).join('\n');
const AsyncFunction = Object.getPrototypeOf(async function(){}).constructor;
const execute = new AsyncFunction('github', 'context', 'core', script);
const sha = 'a'.repeat(40), repo = {owner:'ashugar',repo:'Stardeus-Mod-Maintenance'};
const fullName = `${repo.owner}/${repo.repo}`;
let checks = 0;
async function test(options = {}) {
  const writes = [], calls = [];
  const current = {state:'open',base:{ref:'main',repo:{full_name:fullName}},head:{sha},changed_files:options.files?.length ?? 2,labels:[{name:'manual-label'}],...options.pr};
  let reads=0;
  const map={SchemaVersion:1, Mods:[{Folder:'QuantumStorage',Label:'mod:QuantumStorage'},{Folder:'Other(v0.15Update)',Label:'mod:Other(v0.15Update)'}],AutomationLabel:'repository-automation',...options.map};
  const github = {rest:{repos:{getContent:async args=>{calls.push(args);return {data:{type:'file',encoding:'base64',content:Buffer.from(JSON.stringify(map)).toString('base64')}}},listPullRequestsAssociatedWithCommit:'associated'},pulls:{get:async ()=>({data:++reads===2&&options.race?{...current,head:{sha:'b'.repeat(40)}}:current}),listFiles:'files'},issues:{addLabels:async args=>writes.push(args)}},paginate:async (method)=>method==='associated'?[{number:42}]:options.files??[{filename:'QuantumStorage/README.md'},{filename:'README.md'}]};
  const context={repo,sha:'c'.repeat(40),payload:{workflow_run:{event:'pull_request',repository:{full_name:fullName},head_sha:sha,pull_requests:options.fallback?[]:[{number:42}]}}};
  await execute(github,context,{info:()=>{}});
  assert.equal(calls[0].ref,context.sha,'mapping not pinned to trusted default SHA');checks++;
  return writes;
}
(async()=>{
  let writes=await test();assert.deepEqual(writes[0].labels,['mod:QuantumStorage','repository-automation']);checks++;
  writes=await test({files:[{filename:'Other(v0.15Update)/Code/x.cs',previous_filename:'QuantumStorage/Code/x.cs'}]});assert.deepEqual(writes[0].labels,['mod:QuantumStorage','mod:Other(v0.15Update)']);checks++;
  writes=await test({fallback:true});assert.equal(writes.length,1);checks++;
  writes=await test({pr:{head:{sha:'b'.repeat(40)}}});assert.equal(writes.length,0);checks++;
  writes=await test({pr:{state:'closed'}});assert.equal(writes.length,0);checks++;
  writes=await test({pr:{base:{ref:'elsewhere',repo:{full_name:fullName}}}});assert.equal(writes.length,0);checks++;
  writes=await test({race:true});assert.equal(writes.length,0);checks++;
  writes=await test({pr:{labels:[{name:'mod:QuantumStorage'},{name:'repository-automation'},{name:'manual-label'}]}});assert.equal(writes.length,0);checks++;
  await assert.rejects(()=>test({pr:{changed_files:3001}}));checks++;
  await assert.rejects(()=>test({map:{Mods:[{Folder:'../escape',Label:'mod:../escape'}]}}));checks++;
  await assert.rejects(()=>test({pr:{changed_files:3}}));checks++;
  console.log(`PASS PR label workflow: ${checks} checks`);
})().catch(error=>{console.error(error);process.exitCode=1});
