/* =====================================================================
   AB2 motion-study kit.

   ONE stage, five animation languages. Every study page supplies nothing
   but a table of pose functions; this file owns the map, the real link
   textures, the pawn, the timeline, the Perspective Shift view and the
   C# port panel.

   THE POSE FUNCTIONS ARE WRITTEN IN A RESTRICTED DIALECT ON PURPOSE.
   They use the same helper names as ABStairAnim.cs (Smooth, StepEase,
   Vanish, Lerp, Clamp01, Sin, Abs, ...) and the same field names as
   ClipPose (offX, offZ, rot, sx, sz, facing, hide), so "porting" is a
   mechanical transliteration - which the port panel performs live, from
   the very function that is animating on screen.
   ===================================================================== */
var AB2 = (function () {
"use strict";

/* ---------------------------------------------------------- math (C# names) */
function Smooth(t){ if(t<=0)return 0; if(t>=1)return 1; return t*t*(3-2*t); }
function Clamp01(v){ return v<0?0:(v>1?1:v); }
function Lerp(a,b,t){ return a+(b-a)*t; }
function Abs(v){ return Math.abs(v); }
function Sin(v){ return Math.sin(v); }
function Cos(v){ return Math.cos(v); }
function Min(a,b){ return a<b?a:b; }
function Max(a,b){ return a>b?a:b; }
function FloorToInt(v){ return Math.floor(v); }
var PI = Math.PI;

/* Dwell-pull-dwell staircase easing: n discrete advances. Already in ABStairAnim. */
function StepEase(p,n){
  p=Clamp01(p);
  var s=p*n, i=FloorToInt(s);
  if(i>=n) i=n-1;
  var f=s-i;
  var m=Smooth(Clamp01((f-0.18)/0.55));
  return (i+m)/n;
}
/* Scale-only stand-in for a fade OUT. Already in ABStairAnim. */
function Vanish(p){ return p>0.88 ? 1-0.9*Smooth((p-0.88)/0.12) : 1; }
/* Scale-only stand-in for a fade IN. NEW - the mirror of Vanish. */
function Reveal(q){ return q<0.12 ? 0.10+0.90*Smooth(q/0.12) : 1; }

/* Facing. Rot4: north 0, east 1, south 2, west 3. */
var FaceNone=-1, FaceNorth=0, FaceEast=1, FaceSouth=2, FaceWest=3;
function FaceOf(dx,dz){
  if(Abs(dx)>Abs(dz)){ return dx>0?FaceEast:FaceWest; }
  return dz>0?FaceNorth:FaceSouth;
}
function Opposite(f){
  if(f===FaceNorth)return FaceSouth;
  if(f===FaceSouth)return FaceNorth;
  if(f===FaceEast)return FaceWest;
  if(f===FaceWest)return FaceEast;
  return FaceNone;
}
function NewPose(){
  return {offX:0,offZ:0,rot:0,sx:1,sz:1,hide:false,facing:FaceNone};
}

/* The mod's established depth grammar. Shipped values, unchanged. */
var ScaleBelow=0.32, ScaleAbove=1.14, ElevBelow=0.25, ElevAbove=1.15;

/* --------------------------------------------------------------- textures */
var TEXROOT='../Textures/Things/Building/';
var TYPES={
  stairs  :{label:'stairs',        size:2, multi:true,  hasWest:true,
            down:'AB_StairsDown',      up:'AB_StairsUp',      defDown:'AB2_StairsDown'},
  grand   :{label:'grand stairs',  size:3, multi:true,  hasWest:false,
            down:'AB_GrandStairsDown', up:'AB_GrandStairsUp', defDown:'AB2_GrandStairsDown'},
  ladder  :{label:'ladder',        size:1, multi:true,  hasWest:false,
            down:'AB_LadderDown',      up:'AB_LadderUp',      defDown:'AB2_LadderDown'},
  elevator:{label:'elevator',      size:2, multi:false, hasWest:false,
            down:'AB_Elevator',        up:'AB_Elevator',      defDown:'AB2_Elevator'}
};
var TYPE_ORDER=['stairs','grand','ladder','elevator'];
var ROTNAME=['north','east','south','west'];

/* Which PNG, and whether it must be mirrored (Graphic_Multi mirrors east into west). */
function texFor(type,goingDown,rot){
  var t=TYPES[type];
  var base=goingDown?t.down:t.up;
  if(!t.multi){ return {src:TEXROOT+base+'.png',flip:false}; }
  if(rot===FaceWest && !t.hasWest){ return {src:TEXROOT+base+'_east.png',flip:true}; }
  return {src:TEXROOT+base+'_'+ROTNAME[rot]+'.png',flip:false};
}

/* ------------------------------------------------------------- pawn sprite */
var SKIN='#d9a06a', HAIR='#4a3423', SHIRT='#7b8a93', PANT='#4c5a63', LINE='#2b3238';
function pawnSVG(face){
  var s='<svg width="44" height="52" viewBox="0 0 44 52">';
  /* legs + body, identical for all facings apart from width */
  var bw = (face===FaceEast||face===FaceWest) ? 16 : 20;
  s+='<rect x="'+(22-bw/2+2)+'" y="34" width="'+(bw-4)+'" height="9" rx="2" fill="'+PANT+'"/>';
  s+='<rect x="'+(22-bw/2)+'" y="21" width="'+bw+'" height="15" rx="4" fill="'+SHIRT+'" stroke="'+LINE+'" stroke-width="1"/>';
  if(face===FaceEast||face===FaceWest){
    /* one visible arm, forward of the torso */
    s+='<rect x="'+(face===FaceEast?28:12)+'" y="24" width="4" height="10" rx="2" fill="'+SHIRT+'" stroke="'+LINE+'" stroke-width=".7"/>';
  }
  /* head */
  s+='<circle cx="22" cy="14" r="10.5" fill="'+SKIN+'" stroke="'+LINE+'" stroke-width="1"/>';
  if(face===FaceSouth){
    s+='<path d="M11.6 12.4a10.5 10.5 0 0 1 20.8 0 l0 -2 a10.5 10.5 0 0 0 -20.8 0 z" fill="'+HAIR+'"/>';
    s+='<path d="M11.5 13c0-6 4.7-9.5 10.5-9.5S32.5 7 32.5 13c-1.5-3.4-5-5.2-10.5-5.2S13 9.6 11.5 13z" fill="'+HAIR+'"/>';
    s+='<circle cx="18.2" cy="15.6" r="1.5" fill="#25313a"/><circle cx="25.8" cy="15.6" r="1.5" fill="#25313a"/>';
  } else if(face===FaceNorth){
    s+='<circle cx="22" cy="14" r="10.5" fill="'+HAIR+'"/>';
    s+='<path d="M14 21.5c2.4 1.6 5 2.4 8 2.4s5.6-.8 8-2.4" fill="none" stroke="#3a2a1c" stroke-width="1.4"/>';
  } else {
    var mir = (face===FaceWest);
    s+='<g transform="'+(mir?'translate(44,0) scale(-1,1)':'')+'">';
    s+='<path d="M11.5 14.5c0-6.6 4.2-11 10.5-11 3.4 0 6 1.3 7.6 3.3-2.6-.7-9.5-.6-12.2 3.2-1.7 2.4-1.8 5.4-1.6 8.2-2.3-.6-4.3-1.9-4.3-3.7z" fill="'+HAIR+'"/>';
    s+='<circle cx="27.4" cy="15.4" r="1.5" fill="#25313a"/>';
    s+='</g>';
  }
  return s+'</svg>';
}

/* ------------------------------------------------------------------ helpers */
function $(id){ return document.getElementById(id); }
function el(tag,cls,html){
  var d=document.createElement(tag);
  if(cls)d.className=cls;
  if(html!=null)d.innerHTML=html;
  return d;
}

/* ------------------------------------------------------------- C# port view */
function csharpify(fn,header){
  var s=String(fn);
  s=s.replace(/^function\s+Entry\s*\([^)]*\)\s*\{/,
              'private static ClipPose EntryPose(Clip c, float p)\n{');
  s=s.replace(/^function\s+Emerge\s*\([^)]*\)\s*\{/,
              'private static ClipPose EmergePose(Clip c, float q)\n{');
  s=s.replace(/^function\s+Rig\s*\([^)]*\)\s*\{/,
              'private static RigPose RigPose(Clip c, float p)\n{');
  s=s.replace(/var o\s*=\s*NewPose\(\);/,'ClipPose o = default;\n    o.sx = o.sz = 1f;');
  s=s.replace(/var r\s*=\s*NewRig\(\);/,'RigPose r = default;\n    r.sx = r.sz = 1f;');
  s=s.replace(/\bc\.dirX\b/g,'c.glideDir.x').replace(/\bc\.dirZ\b/g,'c.glideDir.z');
  s=s.replace(/\bc\.ox\b/g,'(c.farAnchor.x - c.landing.x)');
  s=s.replace(/\bc\.oz\b/g,'(c.farAnchor.z - c.landing.z)');
  s=s.replace(/\bPI\b/g,'Mathf.PI');
  s=s.replace(/\b(Sin|Cos|Abs|Min|Max|Lerp|Clamp01|FloorToInt)\(/g,'Mathf.$1(');
  s=s.replace(/===/g,'==').replace(/!==/g,'!=');
  s=s.replace(/\bvar\b/g,'float');
  s=s.replace(/(\d+\.\d+)(?![\dfF])/g,'$1f');
  if(header){ s=header+'\n'+s; }
  return s;
}
function esc(t){
  return t.replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;');
}

/* ============================================================ the stage ==== */
var CELL=54, COLS=7, ROWS=5;

function makePanel(host,skyClass){
  var wrap=el('div','panel');
  var title=el('div','panelTitle','<span class="t"></span>');
  var map=el('div','map'+(skyClass?' sky':''));
  for(var r=0;r<ROWS;r++)for(var c=0;c<COLS;c++){
    var d=el('div','cell '+(((r+c)%2)?'c1':'c2'));
    d.style.left=(c*CELL)+'px'; d.style.top=(r*CELL)+'px';
    map.appendChild(d);
  }
  var bld=el('img','bld'); bld.alt=''; map.appendChild(bld);
  var blobs=[],i;
  for(i=0;i<4;i++){ var b=el('div','blob'); map.appendChild(b); blobs.push(b); }
  var fx=[];
  for(i=0;i<6;i++){ var f=el('div','dust'); map.appendChild(f); fx.push({e:f,born:-999,kind:'dust'}); }
  var shadow=el('div','shadow'); map.appendChild(shadow);
  var actor=el('div','actor'); map.appendChild(actor);
  var cap=el('div','caption');
  wrap.appendChild(title); wrap.appendChild(map); wrap.appendChild(cap);
  host.appendChild(wrap);
  return {root:wrap,title:title.querySelector('.t'),titleBar:title,map:map,bld:bld,
          blobs:blobs,fx:fx,shadow:shadow,actor:actor,cap:cap,face:-2,fxNext:0};
}

function setActor(P,ux,uz,pose,alpha){
  if(alpha<=0 || (pose && pose.hide)){
    P.actor.style.opacity='0'; P.shadow.style.opacity='0'; return;
  }
  var face = (pose && pose.facing!==FaceNone) ? pose.facing : FaceSouth;
  if(P.face!==face){ P.actor.innerHTML=pawnSVG(face); P.face=face; }
  var x=ux*CELL, y=uz*CELL;
  var sx=pose?pose.sx:1, sz=pose?pose.sz:1, rot=pose?pose.rot:0;
  P.actor.style.opacity=String(alpha);
  P.actor.style.transform='translate('+x+'px,'+y+'px) rotate('+rot+'deg) scale('+sx+','+sz+')';
  var g=Min(1,(sx+sz)/2);
  P.shadow.style.opacity=String(alpha*0.5*g);
  P.shadow.style.transform='translate('+x+'px,'+(y+0.20*CELL)+'px) scale('+(sx*g)+','+(sz*g)+')';
}

function setBuilding(P,type,goingDown,rot,rig){
  var t=TYPES[type], tex=texFor(type,goingDown,rot);
  if(P.bld.getAttribute('src')!==tex.src){ P.bld.setAttribute('src',tex.src); }
  var px=t.size*CELL;
  P.bld.style.width=px+'px'; P.bld.style.height=px+'px';
  var cx=P.cx*CELL, cy=P.cy*CELL;
  var ox=rig?rig.offX:0, oz=rig?rig.offZ:0;
  var sx=rig?rig.sx:1, sz=rig?rig.sz:1, rr=rig?rig.rot:0;
  P.bld.style.left=(cx-px/2)+'px'; P.bld.style.top=(cy-px/2)+'px';
  P.bld.style.transform='translate('+(ox*CELL)+'px,'+(-oz*CELL)+'px) rotate('+rr+'deg) scale('+
                        (sx*(tex.flip?-1:1))+','+sz+')';
  var dark=rig?rig.dark:0;
  P.bld.style.filter=dark>0?('brightness('+(1-0.55*dark)+') saturate('+(1-0.25*dark)+')'):'none';
}

function setBlobs(P,list){
  for(var i=0;i<P.blobs.length;i++){
    var b=P.blobs[i], d=list&&list[i];
    if(!d){ b.style.opacity='0'; continue; }
    b.style.opacity=String(d.a);
    b.style.transform='translate('+(d.x*CELL)+'px,'+(d.z*CELL)+'px) scale('+d.s+')';
  }
}

function popFx(P,ux,uz,kind,size,now){
  var slot=P.fx[P.fxNext%P.fx.length]; P.fxNext++;
  slot.born=now; slot.kind=kind; slot.size=size||1;
  slot.x=ux; slot.z=uz;
  slot.e.className=(kind==='ring')?'ring':'dust';
}
function tickFx(P,now){
  for(var i=0;i<P.fx.length;i++){
    var f=P.fx[i], age=(now-f.born)/1000;
    if(f.born<-900 || age>0.75){ f.e.style.opacity='0'; continue; }
    var t=age/0.75;
    var s=(f.kind==='ring')?(0.4+2.6*t)*f.size:(0.55+1.15*t)*f.size;
    f.e.style.opacity=String((1-t)*(f.kind==='ring'?0.85:0.7));
    f.e.style.transform='translate('+(f.x*CELL)+'px,'+((f.z-0.22*t)*CELL)+'px) scale('+s+')';
  }
}

/* ================================================================== boot === */
function boot(cfg){
  document.title='AB2 motion study - '+cfg.name;

  /* ---- chrome ---- */
  var head=el('header');
  head.appendChild(el('h1','','<span class="letter">'+cfg.id+'</span>'+cfg.name));
  head.appendChild(el('p','tag',cfg.tagline));
  document.body.appendChild(head);

  var pages=[['A','anim-A-doorway.html','Doorway'],
             ['B','anim-B-weight.html','Weight'],
             ['C','anim-C-traveler.html','Traveler'],
             ['D','anim-D-machinery.html','Machinery'],
             ['E','anim-E-beat.html','Beat']];
  var navHtml='Animation languages: ';
  pages.forEach(function(pg,i){
    if(i)navHtml+='<span class="sep">&middot;</span>';
    navHtml += (pg[0]===cfg.id)
      ? ('<b>'+pg[0]+' - '+pg[2]+'</b>')
      : ('<a href="'+pg[1]+'">'+pg[0]+' - '+pg[2]+'</a>');
  });
  navHtml+='<span class="sep">|</span>legacy per-type studies: <a href="stairs.html">stairs</a> '+
           '<span class="sep">&middot;</span><a href="ladder.html">ladder</a> '+
           '<span class="sep">&middot;</span><a href="elevator.html">elevator</a>';
  document.body.appendChild(el('nav','',navHtml));

  if(cfg.intro){ var ib=el('div','bar'); ib.style.display='block';
                 ib.innerHTML=cfg.intro; document.body.appendChild(ib); }

  /* ---- control bar ---- */
  var bar=el('div','bar');
  bar.innerHTML=
    '<span class="lbl">Link</span><span class="grp" id="gType"></span>'+
    '<span class="lbl">Journey</span><span class="grp">'+
      '<button id="bDown">&#9660; down</button><button id="bUp">&#9650; up</button></span>'+
    '<span class="lbl">Approach from</span><span class="grp">'+
      '<button data-ap="2">S</button><button data-ap="1">E</button>'+
      '<button data-ap="0">N</button><button data-ap="3">W</button></span>'+
    '<span class="lbl">View</span><span class="grp">'+
      '<button id="bBoth">both bands</button><button id="bPS">Perspective Shift</button></span>'+
    '<span class="latbox" id="latbox">frozen for <b id="latT">0</b>t / <b id="latS">0.00</b>s</span>';
  document.body.appendChild(bar);

  var bar2=el('div','bar');
  bar2.innerHTML=
    '<button id="bPlay">Pause</button><button id="bRestart">Restart</button>'+
    '<button id="bLoop" class="on">Loop</button>'+
    '<span class="ctl">Speed <input type="range" id="sSpeed" min="10" max="150" value="65">'+
      '<b id="vSpeed">0.65</b>x</span>'+
    '<span class="ctl">Step <button id="bBack">&#8592;</button><button id="bFwd">&#8594;</button></span>'+
    '<span class="ctl" style="margin-left:auto">keys: 1-4 link &middot; &#8593;/&#8595; journey &middot; space play</span>';
  document.body.appendChild(bar2);

  var stage=el('div','stage'); document.body.appendChild(stage);
  var PA=makePanel(stage,false);
  var gut=el('div','gutter','<div class="line"></div><div class="arrow" id="gArrow">&#9660;</div><div class="line"></div>');
  stage.appendChild(gut);
  var PB=makePanel(stage,false);

  var tl=el('div','timeline');
  tl.innerHTML='<div class="segbar" id="segbar"></div>'+
    '<div class="readout">t = <b id="rT">0</b> ticks &middot; phase <b id="rPhase">-</b> '+
    '&middot; p = <b id="rP">0.00</b> &middot; clip total <b id="rTot">0</b>t '+
    '(<b id="rSec">0.00</b>s)</div>'+
    '<div class="legend">Hatched segments are ticks during which the pawn is immobilised '+
    '(vanilla StaggerFor). That is the whole felt cost of the effect.</div>';
  document.body.appendChild(tl);

  var port=el('div','port');
  port.innerHTML='<div class="head"><h3>C# port of the clip you are watching</h3>'+
    '<button id="bPort">hide</button></div>'+
    '<pre id="portPre"></pre>'+
    '<p class="warn">Mechanical transliteration of the live JS - review types before pasting. '+
    'Helpers <code>Smooth</code>, <code>StepEase</code>, <code>Vanish</code> already exist in '+
    'ABStairAnim; <code>Reveal</code>, <code>FaceOf</code>, <code>Opposite</code> and the '+
    '<code>ClipPose.facing</code> field are the new additions listed at the top of the dump.</p>';
  document.body.appendChild(port);

  var foot=el('footer'); foot.innerHTML=cfg.footer||''; document.body.appendChild(foot);

  /* ---- state ---- */
  var state={t:0,playing:true,speed:0.65,type:'stairs',down:true,ap:FaceSouth,
             view:'both',loop:true,showPort:true};
  var gType=$('gType');
  TYPE_ORDER.forEach(function(k){
    var b=el('button',null,TYPES[k].label);
    b.dataset.type=k; gType.appendChild(b);
  });

  /* ---- geometry ---- */
  function clip(){ return cfg.clips[state.type]; }
  function dir(){
    /* unit vector pointing FROM the approach side INTO the link, RimWorld axes (+z = north) */
    if(state.ap===FaceSouth) return {x:0,z:1};
    if(state.ap===FaceNorth) return {x:0,z:-1};
    if(state.ap===FaceWest)  return {x:1,z:0};
    return {x:-1,z:0};
  }
  function layout(){
    var t=TYPES[state.type];
    /* building centre in corner units; 2x2 lands on a corner, 1x1 and 3x3 on a cell centre */
    var cx=(t.size%2===0)?4:3.5, cy=(t.size%2===0)?2:2.5;
    PA.cx=cx; PA.cy=cy; PB.cx=cx; PB.cy=cy;
    var d=dir(), out=t.size/2+0.5;
    return {
      cx:cx, cy:cy, d:d,
      /* origin: pawn walks from start to the mouth */
      startU:{x:cx-d.x*2.6, y:cy+d.z*2.6},
      mouthU:{x:cx, y:cy},
      /* destination: pawn appears at the far mouth and lands one cell beyond it */
      landU:{x:cx+d.x*out, y:cy-d.z*out},
      exitU:{x:cx+d.x*(out+2.2), y:cy-d.z*(out+2.2)},
      out:out
    };
  }
  function ctx(){
    var L=layout();
    return {up:!state.down, dirX:L.d.x, dirZ:L.d.z,
            ox:-L.d.x*L.out, oz:-L.d.z*L.out, L:L};
  }

  /* ---- timeline ---- */
  function phases(){
    var cl=clip(), lat=!cl.noStagger;
    var ps=[{id:'approach',t:34,label:'walk to the link',col:'#3b4656'}];
    if(cl.dual){
      ps.push({id:'cross',t:cl.entry,label:'crossing - drawn on BOTH bands',
               col:'#7a6ab0',lat:lat});
    }else{
      ps.push({id:'entry',t:cl.entry,label:'entry clip (ghost at origin mouth)',
               col:'#b0813f',lat:lat});
      if(cl.hold>0) ps.push({id:'hold',t:cl.hold,label:'hold - nobody drawn',
                             col:'#8f4e4e',lat:lat});
      ps.push({id:'emerge',t:cl.emerge,label:'emerge clip (destination)',col:'#5f8f52'});
    }
    ps.push({id:'depart',t:34,label:'walk on',col:'#3b4656'});
    ps.push({id:'idle',t:20,label:'loop pause',col:'#242830'});
    return ps;
  }
  function total(){ var ps=phases(),s=0,i; for(i=0;i<ps.length;i++)s+=ps[i].t; return s; }
  function latency(){
    var cl=clip();
    if(cl.noStagger) return 0;
    return cl.dual ? cl.entry : (cl.entry+cl.hold);
  }
  function clipTotal(){ var cl=clip(); return cl.dual?cl.entry:(cl.entry+cl.hold+cl.emerge); }

  function buildBar(){
    var bar=$('segbar'); bar.innerHTML='';
    var ps=phases(), tt=total(), i;
    for(i=0;i<ps.length;i++){
      var s=el('div','seg'+(ps[i].lat?' lat':''));
      s.style.width=(ps[i].t/tt*100)+'%';
      s.style.background=ps[i].col;
      s.title=ps[i].label+' - '+ps[i].t+'t';
      if(ps[i].t/tt>0.13) s.appendChild(el('div','seglabel',ps[i].t+'t'));
      bar.appendChild(s);
    }
    bar.appendChild(el('div','playhead'));
    var lt=latency();
    var lb=$('latbox');
    lb.className='latbox'+(lt===0?' zero':'');
    lb.innerHTML=(lt===0?'never frozen ':'frozen for <b>'+lt+'</b>t / <b>'+
                 (lt/60).toFixed(2)+'</b>s');
    $('rTot').textContent=clipTotal();
    $('rSec').textContent=(clipTotal()/60).toFixed(2);
  }

  /* ---- port panel ---- */
  var PORT_HEAD=
    '// ==== ONE-TIME ADDITIONS TO ABStairAnim ====================================\n'+
    '//   struct ClipPose  += public int facing;   // -1 = leave vanilla facing alone\n'+
    '//   private const int FaceNone = -1, FaceNorth = 0, FaceEast = 1,\n'+
    '//                     FaceSouth = 2, FaceWest = 3;\n'+
    '//   private static float Reveal(float q)\n'+
    '//       => q < 0.12f ? 0.10f + 0.90f * Smooth(q / 0.12f) : 1f;\n'+
    '//   private static int FaceOf(float dx, float dz)\n'+
    '//       => Mathf.Abs(dx) > Mathf.Abs(dz) ? (dx > 0f ? FaceEast : FaceWest)\n'+
    '//                                        : (dz > 0f ? FaceNorth : FaceSouth);\n'+
    '//   private static int Opposite(int f) => f == FaceNorth ? FaceSouth\n'+
    '//       : f == FaceSouth ? FaceNorth : f == FaceEast ? FaceWest\n'+
    '//       : f == FaceWest ? FaceEast : FaceNone;\n'+
    '//\n'+
    '// ==== ONE-TIME ADDITION TO Patch_PawnRenderer_ABBelowShrink.Postfix =========\n'+
    '//   ...right after the pose.hide branch, before the matrix multiply:\n'+
    '//       if (pose.facing != FaceNone) { __result.facing = new Rot4(pose.facing); }\n'+
    '//   ShouldRecache already compares facing, and IsAnimating already vetoes the\n'+
    '//   cached atlas blit for the whole clip, so nothing else has to change.\n';

  function refreshPort(){
    var cl=clip();
    var txt=PORT_HEAD+'\n'+
      '// ==== '+cfg.name.toUpperCase()+' / '+TYPES[state.type].label.toUpperCase()+
      ' ('+TYPES[state.type].defDown+') ====\n'+
      '// durations, ticks:  entry '+cl.entry+'   hold '+cl.hold+'   emerge '+cl.emerge+
      (cl.dual?'   [DUAL: emergeStart = 0 at clip creation]':'')+
      (cl.noStagger?'   [NO STAGGER: staggerTicks = 0]':'')+'\n'+
      (cl.portNote?('// '+cl.portNote.split('\n').join('\n// ')+'\n'):'')+
      '\n'+csharpify(cl.entryPose)+'\n\n'+csharpify(cl.emergePose)+
      (cl.rig?('\n\n// Structure track - needs an owned draw for the link building (rule 38).\n'+
               csharpify(cl.rig)):'');
    $('portPre').innerHTML=esc(txt).replace(/(\/\/[^\n]*)/g,'<span class="cmt">$1</span>');
  }

  /* ---- text ---- */
  function refreshText(){
    var cl=clip(), t=TYPES[state.type];
    var dn=state.down;
    PA.title.innerHTML=(dn?'Origin - level 2 ':'Origin - level 1 ')+
      '<small>('+t.label+(dn?' down':' up')+', viewed top-down)</small>';
    PB.title.innerHTML=(dn?'Destination - level 1 ':'Destination - level 2 ')+
      '<small>(the counterpart '+t.label+')</small>';
    PA.cap.innerHTML=cl.capO||'';
    PB.cap.innerHTML=cl.capD||'';
    $('gArrow').innerHTML=dn?'&#9660;':'&#9650;';
    $('bDown').classList.toggle('sel',dn);
    $('bUp').classList.toggle('sel',!dn);
    $('bBoth').classList.toggle('sel',state.view==='both');
    $('bPS').classList.toggle('sel',state.view==='ps');
    Array.prototype.forEach.call(document.querySelectorAll('[data-ap]'),function(b){
      b.classList.toggle('sel',+b.dataset.ap===state.ap); });
    Array.prototype.forEach.call(document.querySelectorAll('[data-type]'),function(b){
      b.classList.toggle('sel',b.dataset.type===state.type); });
    buildBar(); refreshPort();
  }

  /* ---- the frame ---- */
  var last=performance.now();
  function frame(now){
    var dt=(now-last)/1000; last=now;
    var ps=phases(), tt=total();
    if(state.playing){
      state.t+=dt*60*state.speed;
      if(state.t>=tt){ state.t = state.loop ? state.t-tt : tt-0.001; }
    }
    var t=state.t, acc=0, cur=ps[0], p=0, i;
    for(i=0;i<ps.length;i++){
      if(t<acc+ps[i].t || i===ps.length-1){ cur=ps[i]; p=ps[i].t>0?(t-acc)/ps[i].t:1; break; }
      acc+=ps[i].t;
    }
    p=Clamp01(p);

    var c=ctx(), L=c.L, cl=clip();
    var goingDown=state.down;

    /* building art: origin shows the link you entered, destination its counterpart */
    var rigO=null, rigD=null;
    if(cl.rig){
      if(cur.id==='entry'||cur.id==='cross') rigO=cl.rig(c,p,false);
      if(cur.id==='emerge'||cur.id==='cross') rigD=cl.rig(c,cur.id==='cross'?p:p,true);
    }
    setBuilding(PA,state.type,goingDown,state.ap,rigO);
    setBuilding(PB,state.type,!goingDown,state.ap,rigD);
    setBlobs(PA,rigO&&rigO.blobs); setBlobs(PB,rigD&&rigD.blobs);

    /* pawn */
    var poseO=null, poseD=null, uO=null, uD=null;
    if(cur.id==='approach'){
      var w=p, bob=Abs(Sin(p*6*PI));
      uO={x:Lerp(L.startU.x,L.mouthU.x,w), y:Lerp(L.startU.y,L.mouthU.y,w)-bob*0.05};
      poseO=NewPose(); poseO.facing=FaceOf(c.dirX,c.dirZ); poseO.rot=Sin(p*6*PI)*3;
    } else if(cur.id==='entry'){
      poseO=cl.entryPose(c,p); uO={x:L.mouthU.x,y:L.mouthU.y};
    } else if(cur.id==='cross'){
      poseO=cl.entryPose(c,p);  uO={x:L.mouthU.x,y:L.mouthU.y};
      poseD=cl.emergePose(c,p); uD={x:L.landU.x, y:L.landU.y};
    } else if(cur.id==='hold'){
      /* nobody anywhere - that is the point of the segment */
    } else if(cur.id==='emerge'){
      poseD=cl.emergePose(c,p); uD={x:L.landU.x,y:L.landU.y};
    } else if(cur.id==='depart'){
      var w2=p, bob2=Abs(Sin(p*6*PI));
      uD={x:Lerp(L.landU.x,L.exitU.x,w2), y:Lerp(L.landU.y,L.exitU.y,w2)-bob2*0.05};
      poseD=NewPose(); poseD.facing=FaceOf(c.dirX,c.dirZ); poseD.rot=Sin(p*6*PI)*3;
    }
    if(poseO&&uO){ setActor(PA,uO.x+poseO.offX,uO.y-poseO.offZ,poseO,1); }
    else setActor(PA,0,0,null,0);
    if(poseD&&uD){ setActor(PB,uD.x+poseD.offX,uD.y-poseD.offZ,poseD,1); }
    else setActor(PB,0,0,null,0);

    /* fx */
    if(cl.fx){
      for(i=0;i<cl.fx.length;i++){
        var f=cl.fx[i];
        if(f.phase!==cur.id) continue;
        var key=cur.id+'#'+i;
        if(p>=f.at && fired[key]!==Math.floor(state.loopCount||0)){
          fired[key]=Math.floor(state.loopCount||0);
          var P=(f.where==='origin')?PA:PB;
          var at=(f.where==='origin')?L.mouthU:L.landU;
          popFx(P,at.x+(f.dx||0),at.y-(f.dz||0),f.kind||'dust',f.size||1,now);
        }
        if(p<f.at) fired[key]=-1;
      }
    }
    tickFx(PA,now); tickFx(PB,now);

    /* Perspective Shift view: one camera, and it has to cut somewhere */
    var camOnOrigin;
    if(cl.dual){ camOnOrigin = !(cur.id==='cross'&&p>0.5) && cur.id!=='emerge' && cur.id!=='depart'; }
    else { camOnOrigin = (cur.id==='approach'||cur.id==='entry'||
                          (cur.id==='hold'&&p<0.5)); }
    if(state.view==='ps'){
      PA.root.style.display=camOnOrigin?'':'none';
      PB.root.style.display=camOnOrigin?'none':'';
      gut.style.display='none';
      var frozen=(cur.id==='entry'||cur.id==='hold'||(cur.id==='cross'&&!cl.noStagger));
      var badge=camOnOrigin?PA:PB;
      var other=camOnOrigin?PB:PA;
      tagCam(badge,true,frozen); tagCam(other,false,false);
    }else{
      PA.root.style.display=''; PB.root.style.display=''; gut.style.display='';
      tagCam(PA,false,false); tagCam(PB,false,false);
      PA.root.classList.toggle('dim',!(cur.id==='approach'||cur.id==='entry'||cur.id==='cross'));
      PB.root.classList.toggle('dim',(cur.id==='approach'||cur.id==='entry'));
    }

    /* readouts */
    var ph=document.querySelector('.playhead');
    if(ph) ph.style.left=(t/tt*100)+'%';
    $('rT').textContent=Math.round(t);
    $('rPhase').textContent=cur.label;
    $('rP').textContent=p.toFixed(2);
    requestAnimationFrame(frame);
  }
  var fired={};

  function tagCam(P,isCam,frozen){
    var bar=P.titleBar, pill=bar.querySelector('.pill');
    if(!isCam){ if(pill)pill.remove(); return; }
    if(!pill){ pill=el('span','pill cam'); bar.appendChild(pill); }
    pill.className='pill '+(frozen?'frozen':'cam');
    pill.textContent=frozen?'avatar camera - controls frozen':'avatar camera';
  }

  /* ---- wiring ---- */
  gType.addEventListener('click',function(e){
    var b=e.target.closest('button'); if(!b)return;
    state.type=b.dataset.type; state.t=0; fired={}; refreshText();
  });
  document.querySelectorAll('[data-ap]').forEach(function(b){
    b.addEventListener('click',function(){ state.ap=+b.dataset.ap; state.t=0; refreshText(); });
  });
  $('bDown').onclick=function(){ state.down=true; state.t=0; refreshText(); };
  $('bUp').onclick=function(){ state.down=false; state.t=0; refreshText(); };
  $('bBoth').onclick=function(){ state.view='both'; refreshText(); };
  $('bPS').onclick=function(){ state.view='ps'; refreshText(); };
  $('bPlay').onclick=function(){ state.playing=!state.playing;
                                 $('bPlay').textContent=state.playing?'Pause':'Play'; };
  $('bRestart').onclick=function(){ state.t=0; fired={}; };
  $('bLoop').onclick=function(){ state.loop=!state.loop;
                                 $('bLoop').classList.toggle('on',state.loop); };
  $('bBack').onclick=function(){ state.t=Max(0,state.t-2); };
  $('bFwd').onclick=function(){ state.t=state.t+2; };
  $('sSpeed').oninput=function(){ state.speed=(+this.value)/100;
                                  $('vSpeed').textContent=state.speed.toFixed(2); };
  $('bPort').onclick=function(){
    state.showPort=!state.showPort;
    $('portPre').style.display=state.showPort?'':'none';
    $('bPort').textContent=state.showPort?'hide':'show';
  };
  $('segbar').addEventListener('click',function(e){
    var r=this.getBoundingClientRect();
    state.t=Clamp01((e.clientX-r.left)/r.width)*total();
    state.playing=false; $('bPlay').textContent='Play';
  });
  document.addEventListener('keydown',function(e){
    if(e.key===' '){ $('bPlay').click(); e.preventDefault(); }
    else if(e.key>='1'&&e.key<='4'){ state.type=TYPE_ORDER[+e.key-1]; state.t=0; refreshText(); }
    else if(e.key==='ArrowUp'){ $('bUp').click(); }
    else if(e.key==='ArrowDown'){ $('bDown').click(); }
  });

  refreshText();
  requestAnimationFrame(frame);
}

return {boot:boot, Smooth:Smooth, StepEase:StepEase, Vanish:Vanish, Reveal:Reveal,
        Clamp01:Clamp01, Lerp:Lerp, Abs:Abs, Sin:Sin, Cos:Cos, Min:Min, Max:Max,
        FloorToInt:FloorToInt, PI:PI, NewPose:NewPose, FaceOf:FaceOf, Opposite:Opposite,
        FaceNone:FaceNone, FaceNorth:FaceNorth, FaceEast:FaceEast, FaceSouth:FaceSouth,
        FaceWest:FaceWest, ScaleBelow:ScaleBelow, ScaleAbove:ScaleAbove,
        ElevBelow:ElevBelow, ElevAbove:ElevAbove};
})();

/* Pull the kit's C#-named helpers into global scope so the per-page pose functions
   read EXACTLY like the C# they will become. */
var Smooth=AB2.Smooth, StepEase=AB2.StepEase, Vanish=AB2.Vanish, Reveal=AB2.Reveal,
    Clamp01=AB2.Clamp01, Lerp=AB2.Lerp, Abs=AB2.Abs, Sin=AB2.Sin, Cos=AB2.Cos,
    Min=AB2.Min, Max=AB2.Max, FloorToInt=AB2.FloorToInt, PI=AB2.PI,
    NewPose=AB2.NewPose, FaceOf=AB2.FaceOf, Opposite=AB2.Opposite,
    FaceNone=AB2.FaceNone, FaceNorth=AB2.FaceNorth, FaceEast=AB2.FaceEast,
    FaceSouth=AB2.FaceSouth, FaceWest=AB2.FaceWest,
    ScaleBelow=AB2.ScaleBelow, ScaleAbove=AB2.ScaleAbove,
    ElevBelow=AB2.ElevBelow, ElevAbove=AB2.ElevAbove;

function NewRig(){ return {offX:0,offZ:0,rot:0,sx:1,sz:1,dark:0,blobs:null}; }
