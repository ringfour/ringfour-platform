import { chromium } from "playwright";
import { writeFile } from "node:fs/promises";
import { resolve } from "node:path";
// Read-only deployed runtime check. Uses real public Auth0 configuration, no session or mock auth.
const origin=process.env.CONSOLE_URL ?? "http://localhost:5173";
const html=await (await fetch(origin)).text();
if(html.includes('/@vite/client')) throw new Error("Console still serves Vite development HTML");
const asset=html.match(/src="([^"]+\.js)"/)?.[1];
if(!asset) throw new Error("Compiled JavaScript asset missing");
const response=await fetch(new URL(asset,origin),{headers:{"Accept-Encoding":"gzip"}});
const encoding=response.headers.get("content-encoding");
const compiledBytes=(await response.arrayBuffer()).byteLength;
const assetUrl=new URL(asset,origin);
const {get}=await import(assetUrl.protocol === "https:" ? "node:https" : "node:http");
const transferredBytes=await new Promise((resolve,reject)=>{
  get(assetUrl,{headers:{"Accept-Encoding":"gzip"}},res=>{let size=0;res.on("data",chunk=>{size+=chunk.length;});res.on("end",()=>resolve(size));res.on("error",reject);}).on("error",reject);
});
if(encoding !== "gzip") throw new Error("JavaScript compression is disabled");
const checks={};
for(const [path,expected] of [["/api/health/ready",200],["/api/v1/organizations",401],["/assets/missing.js",404],["/apps/fixture/settings?workspace=fixture",200]]) {
 const status=(await fetch(origin+path)).status;
 if(status !== expected) throw new Error(`Unexpected HTTP ${status}: ${path}`);
 checks[path]=status;
}
const browser=await chromium.launch();
const samples=[];
try {
 for(let index=0;index<3;index++) {
  const context=await browser.newContext();
  const page=await context.newPage();
  const errors=[];page.on("pageerror",error=>errors.push(error.message));
  const started=performance.now();
  await page.goto(origin+"/apps/fixture/settings?workspace=fixture");
  await page.getByRole("button",{name:"Sign in",exact:true}).waitFor();
  samples.push(Math.round(performance.now()-started));
  if(errors.length) throw new Error(errors.join("\n"));
  await context.close();
 }
 const report={origin,production_bundle:true,vite_client:false,gzip:encoding,asset,decoded_javascript_bytes:compiledBytes,compressed_javascript_bytes:transferredBytes,http_checks:checks,cold_sign_in_render_ms:samples,live_authenticated_acceptance:false};
 const path=resolve(new URL('../../../docs/evidence/ap21-browser/deployed-runtime.json',import.meta.url).pathname);
 await writeFile(path,JSON.stringify(report,null,2)+'\n');
 console.log(JSON.stringify(report));
} finally {await browser.close();}
