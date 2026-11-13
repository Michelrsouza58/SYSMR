namespace SYSMR.Infrastructure.LocalServer;

/// <summary>Página web do garçom servida pelo <see cref="LocalWaiterServer"/>.</summary>
internal static class WaiterAppPage
{
    public const string Html = """
<!DOCTYPE html>
<html lang="pt-BR">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1, viewport-fit=cover">
<meta name="theme-color" content="#0f172a">
<title>SYSMR Garçom</title>
<style>
:root{--bg:#0f172a;--card:#1e293b;--card2:#273549;--accent:#38bdf8;--accent2:#0ea5e9;--ok:#22c55e;--danger:#ef4444;--warn:#f59e0b;--text:#f1f5f9;--muted:#94a3b8;--radius:14px}
*{box-sizing:border-box;margin:0;padding:0;-webkit-tap-highlight-color:transparent}
body{font-family:'Segoe UI',system-ui,sans-serif;background:var(--bg);color:var(--text);min-height:100vh}
button{font-family:inherit;cursor:pointer;border:none;border-radius:var(--radius)}
.hidden{display:none!important}
/* Login */
#loginView{display:flex;flex-direction:column;align-items:center;justify-content:center;min-height:100vh;padding:24px}
.login-card{background:var(--card);border-radius:20px;padding:32px 24px;width:100%;max-width:360px;box-shadow:0 20px 60px rgba(0,0,0,.5)}
.login-card h1{font-size:26px;text-align:center;color:var(--accent);letter-spacing:2px}
.login-card .store{text-align:center;color:var(--muted);margin:6px 0 24px;font-size:14px}
.field{margin-bottom:16px}
.field label{display:block;font-size:13px;color:var(--muted);margin-bottom:6px}
.field input{width:100%;padding:14px;border-radius:var(--radius);border:1px solid #334155;background:var(--bg);color:var(--text);font-size:17px;outline:none}
.field input:focus{border-color:var(--accent)}
.btn-primary{width:100%;padding:15px;background:linear-gradient(135deg,var(--accent),var(--accent2));color:#082f49;font-weight:700;font-size:16px}
.btn-primary:active{transform:scale(.98)}
.error-msg{color:var(--danger);font-size:13px;text-align:center;margin-top:12px;min-height:18px}
/* Header */
.topbar{position:sticky;top:0;z-index:10;background:var(--card);padding:12px 16px;display:flex;align-items:center;gap:12px;box-shadow:0 2px 12px rgba(0,0,0,.4)}
.topbar .title{flex:1;font-size:17px;font-weight:600}
.topbar .sub{font-size:12px;color:var(--muted)}
.icon-btn{background:var(--card2);color:var(--text);padding:9px 13px;font-size:15px;border-radius:10px}
/* Areas */
.area-tabs{display:flex;gap:8px;padding:12px 16px;overflow-x:auto}
.area-tab{padding:9px 18px;background:var(--card);color:var(--muted);border-radius:999px;font-size:14px;white-space:nowrap}
.area-tab.active{background:var(--accent);color:#082f49;font-weight:700}
/* Tables */
.tables-grid{display:grid;grid-template-columns:repeat(auto-fill,minmax(100px,1fr));gap:12px;padding:8px 16px 90px}
.table-card{background:var(--card);border-radius:var(--radius);padding:16px 8px;text-align:center;border:2px solid transparent}
.table-card .num{font-size:26px;font-weight:700}
.table-card .st{font-size:11px;margin-top:4px;letter-spacing:1px}
.table-card .tot{font-size:12px;color:var(--muted);margin-top:4px}
.table-card.free{border-color:var(--ok)}.table-card.free .st{color:var(--ok)}
.table-card.busy{border-color:var(--danger)}.table-card.busy .st{color:var(--danger)}
/* Order */
.order-list{padding:8px 16px 170px}
.order-item{background:var(--card);border-radius:var(--radius);padding:12px 14px;margin-bottom:10px;display:flex;gap:10px;align-items:center}
.order-item .info{flex:1}
.order-item .name{font-weight:600;font-size:15px}
.order-item .obs{font-size:12px;color:var(--warn);margin-top:2px}
.order-item .price{font-size:13px;color:var(--muted);margin-top:2px}
.order-item .qty-badge{background:var(--card2);border-radius:8px;padding:4px 10px;font-weight:700;font-size:14px}
.btn-remove{background:transparent;color:var(--danger);font-size:19px;padding:6px 10px}
.order-empty{text-align:center;color:var(--muted);padding:40px 0;font-size:14px}
.order-footer{position:fixed;bottom:0;left:0;right:0;background:var(--card);padding:14px 16px;box-shadow:0 -4px 20px rgba(0,0,0,.5)}
.order-total{display:flex;justify-content:space-between;font-size:18px;font-weight:700;margin-bottom:10px}
/* Menu */
.menu-search{padding:10px 16px}
.menu-search input{width:100%;padding:13px 16px;border-radius:999px;border:1px solid #334155;background:var(--card);color:var(--text);font-size:15px;outline:none}
.cat-tabs{display:flex;gap:8px;padding:0 16px 10px;overflow-x:auto}
.cat-tab{padding:7px 16px;background:var(--card);color:var(--muted);border-radius:999px;font-size:13px;white-space:nowrap}
.cat-tab.active{background:var(--accent2);color:#fff;font-weight:600}
.menu-grid{display:grid;grid-template-columns:repeat(auto-fill,minmax(150px,1fr));gap:10px;padding:4px 16px 110px}
.menu-item{background:var(--card);border-radius:var(--radius);padding:14px 12px;text-align:left;color:var(--text)}
.menu-item .n{font-weight:600;font-size:14px;line-height:1.3}
.menu-item .p{color:var(--accent);font-weight:700;margin-top:6px;font-size:15px}
/* Cart bar */
.cart-bar{position:fixed;bottom:0;left:0;right:0;padding:12px 16px;background:transparent;pointer-events:none}
.cart-bar button{pointer-events:auto;width:100%;padding:16px;background:linear-gradient(135deg,var(--ok),#16a34a);color:#052e16;font-weight:700;font-size:16px;box-shadow:0 8px 24px rgba(34,197,94,.4)}
/* Modal */
.modal-backdrop{position:fixed;inset:0;background:rgba(2,6,23,.8);display:flex;align-items:flex-end;justify-content:center;z-index:50}
.modal{background:var(--card);border-radius:20px 20px 0 0;width:100%;max-width:480px;max-height:88vh;overflow-y:auto;padding:20px}
.modal h3{font-size:18px;margin-bottom:4px}
.modal .price-line{color:var(--accent);font-weight:700;margin-bottom:16px}
.qty-row{display:flex;align-items:center;justify-content:center;gap:18px;margin:14px 0}
.qty-btn{width:52px;height:52px;border-radius:50%;background:var(--card2);color:var(--text);font-size:26px;font-weight:700}
.qty-val{font-size:28px;font-weight:700;min-width:50px;text-align:center}
.opt-row{display:flex;align-items:center;gap:10px;background:var(--card2);border-radius:10px;padding:11px 12px;margin-bottom:8px}
.opt-row .opt-name{flex:1;font-size:14px}
.opt-row .opt-price{color:var(--accent);font-size:13px}
.opt-row input[type=checkbox]{width:22px;height:22px;accent-color:var(--accent)}
.modal textarea{width:100%;padding:12px;border-radius:10px;border:1px solid #334155;background:var(--bg);color:var(--text);font-size:14px;font-family:inherit;resize:none;margin-top:6px}
.modal-actions{display:flex;gap:10px;margin-top:18px}
.btn-ghost{flex:1;padding:14px;background:var(--card2);color:var(--text);font-weight:600}
.btn-add{flex:2;padding:14px;background:linear-gradient(135deg,var(--accent),var(--accent2));color:#082f49;font-weight:700}
/* Toast */
#toast{position:fixed;top:16px;left:50%;transform:translateX(-50%);background:var(--card);border:1px solid var(--accent);color:var(--text);padding:12px 22px;border-radius:999px;font-size:14px;z-index:100;box-shadow:0 8px 30px rgba(0,0,0,.5);transition:opacity .3s}
#toast.err{border-color:var(--danger)}
.offline-badge{background:var(--warn);color:#451a03;font-size:11px;font-weight:700;padding:3px 10px;border-radius:999px}
</style>
</head>
<body>

<div id="loginView">
  <div class="login-card">
    <h1>SYSMR</h1>
    <div class="store" id="storeName">Carregando...</div>
    <div class="field"><label>Número do garçom</label><input id="loginNumber" autocomplete="username" placeholder="Ex: 01"></div>
    <div class="field"><label>PIN</label><input id="loginPin" type="password" inputmode="numeric" autocomplete="off" placeholder="••••"></div>
    <button class="btn-primary" id="loginBtn">Entrar</button>
    <div class="error-msg" id="loginError"></div>
  </div>
</div>

<div id="tablesView" class="hidden">
  <div class="topbar">
    <div style="flex:1"><div class="title" id="waiterName"></div><div class="sub">Mesas <span class="offline-badge">MODO LOCAL</span></div></div>
    <button class="icon-btn" id="refreshTablesBtn">↻</button>
    <button class="icon-btn" id="logoutBtn">Sair</button>
  </div>
  <div class="area-tabs" id="areaTabs"></div>
  <div class="tables-grid" id="tablesGrid"></div>
</div>

<div id="tableView" class="hidden">
  <div class="topbar">
    <button class="icon-btn" id="backToTablesBtn">←</button>
    <div style="flex:1"><div class="title" id="tableTitle"></div><div class="sub" id="tableSub"></div></div>
    <button class="icon-btn" id="refreshOrderBtn">↻</button>
  </div>
  <div class="order-list" id="orderList"></div>
  <div class="order-footer">
    <div class="order-total"><span>Total</span><span id="orderTotal">R$ 0,00</span></div>
    <button class="btn-primary" id="addItemsBtn">＋ Adicionar itens</button>
  </div>
</div>

<div id="menuView" class="hidden">
  <div class="topbar">
    <button class="icon-btn" id="backToOrderBtn">←</button>
    <div style="flex:1"><div class="title" id="menuTitle"></div><div class="sub">Toque para adicionar</div></div>
  </div>
  <div class="menu-search"><input id="menuSearch" placeholder="Buscar no cardápio..."></div>
  <div class="cat-tabs" id="catTabs"></div>
  <div class="menu-grid" id="menuGrid"></div>
  <div class="cart-bar hidden" id="cartBar"><button id="sendCartBtn"></button></div>
</div>

<div id="itemModal" class="modal-backdrop hidden">
  <div class="modal">
    <h3 id="modalItemName"></h3>
    <div class="price-line" id="modalItemPrice"></div>
    <div id="modalOptions"></div>
    <div class="qty-row">
      <button class="qty-btn" id="qtyMinus">−</button>
      <div class="qty-val" id="qtyVal">1</div>
      <button class="qty-btn" id="qtyPlus">＋</button>
    </div>
    <label style="font-size:13px;color:var(--muted)">Observação</label>
    <textarea id="modalObs" rows="2" placeholder="Ex: sem cebola, ponto da carne..."></textarea>
    <div class="modal-actions">
      <button class="btn-ghost" id="modalCancel">Cancelar</button>
      <button class="btn-add" id="modalAdd">Adicionar</button>
    </div>
  </div>
</div>

<div id="toast" class="hidden"></div>

<script>
const $=id=>document.getElementById(id);
const fmt=v=>'R$ '+Number(v).toFixed(2).replace('.',',');
let state={token:localStorage.getItem('sysmr_token'),name:localStorage.getItem('sysmr_name'),areas:[],menu:[],areaId:null,table:null,order:null,cart:[],cat:null,search:''};
let pollTimer=null;

async function api(path,opts={}){
  opts.headers=Object.assign({'Content-Type':'application/json'},opts.headers||{});
  if(state.token)opts.headers['X-Waiter-Token']=state.token;
  const r=await fetch(path,opts);
  const data=await r.json().catch(()=>null);
  if(r.status===401&&state.token&&path!=='/api/login'){doLogout();throw new Error('Sessão expirada. Entre novamente.');}
  if(!r.ok)throw new Error((data&&data.error)||'Erro de comunicação');
  return data;
}
function toast(msg,isErr){
  const t=$('toast');t.textContent=msg;t.className=isErr?'err':'';t.classList.remove('hidden');
  setTimeout(()=>t.classList.add('hidden'),2600);
}
function show(view){
  ['loginView','tablesView','tableView','menuView'].forEach(v=>$(v).classList.add('hidden'));
  $(view).classList.remove('hidden');
  clearInterval(pollTimer);pollTimer=null;
  if(view==='tablesView')pollTimer=setInterval(loadAreas,10000);
  if(view==='tableView')pollTimer=setInterval(loadOrder,5000);
}
function doLogout(){
  state.token=null;state.name=null;localStorage.removeItem('sysmr_token');localStorage.removeItem('sysmr_name');
  show('loginView');
}

/* Login */
$('loginBtn').onclick=async()=>{
  $('loginError').textContent='';
  try{
    const r=await api('/api/login',{method:'POST',body:JSON.stringify({waiterNumber:$('loginNumber').value.trim(),pin:$('loginPin').value})});
    state.token=r.token;state.name=r.name;
    localStorage.setItem('sysmr_token',r.token);localStorage.setItem('sysmr_name',r.name);
    await enterApp();
  }catch(e){$('loginError').textContent=e.message;}
};
$('loginPin').addEventListener('keydown',e=>{if(e.key==='Enter')$('loginBtn').click();});
$('logoutBtn').onclick=doLogout;

async function enterApp(){
  $('waiterName').textContent=state.name||'';
  await loadAreas();
  show('tablesView');
}

/* Mesas */
async function loadAreas(){
  try{
    state.areas=await api('/api/areas');
    if(!state.areaId||!state.areas.some(a=>a.id===state.areaId))state.areaId=state.areas[0]&&state.areas[0].id;
    renderAreas();
  }catch(e){/* silencioso no polling */}
}
function renderAreas(){
  $('areaTabs').innerHTML='';
  state.areas.forEach(a=>{
    const b=document.createElement('button');b.className='area-tab'+(a.id===state.areaId?' active':'');b.textContent=a.name;
    b.onclick=()=>{state.areaId=a.id;renderAreas();};$('areaTabs').appendChild(b);
  });
  const area=state.areas.find(a=>a.id===state.areaId);
  const grid=$('tablesGrid');grid.innerHTML='';
  if(!area||!area.tables.length){grid.innerHTML='<div class="order-empty">Nenhuma mesa nesta área.</div>';return;}
  area.tables.forEach(t=>{
    const c=document.createElement('button');c.className='table-card '+(t.isOccupied?'busy':'free');
    c.innerHTML='<div class="num">'+t.number+'</div><div class="st">'+(t.isOccupied?'OCUPADA':'LIVRE')+'</div>'+(t.isOccupied?'<div class="tot">'+fmt(t.total)+'</div>':'');
    c.onclick=()=>openTable(area,t);grid.appendChild(c);
  });
}
$('refreshTablesBtn').onclick=loadAreas;

/* Comanda */
async function openTable(area,table){
  state.table={areaId:area.id,areaName:area.name,id:table.id,number:table.number};
  $('tableTitle').textContent='Mesa '+table.number;
  await loadOrder();
  show('tableView');
}
async function loadOrder(){
  if(!state.table)return;
  try{
    state.order=await api('/api/tables/'+state.table.id+'/order');
    renderOrder();
  }catch(e){}
}
function renderOrder(){
  const o=state.order,list=$('orderList');
  $('tableSub').textContent=o?('Comanda'+(o.dailyNumber?' Nº '+o.dailyNumber:'')+(o.employeeName?' · '+o.employeeName:'')):'Mesa livre — envie itens para abrir';
  list.innerHTML='';
  if(!o||!o.items.length){list.innerHTML='<div class="order-empty">Nenhum item na comanda.<br>Toque em "Adicionar itens".</div>';}
  else o.items.forEach(i=>{
    const d=document.createElement('div');d.className='order-item';
    d.innerHTML='<div class="qty-badge">'+i.quantity+'x</div><div class="info"><div class="name">'+esc(i.name)+'</div>'+
      (i.observation?'<div class="obs">⚠ '+esc(i.observation)+'</div>':'')+
      '<div class="price">'+fmt(i.unitPrice)+' un · '+fmt(i.total)+'</div></div>';
    const rm=document.createElement('button');rm.className='btn-remove';rm.textContent='🗑';
    rm.onclick=()=>removeItem(i);d.appendChild(rm);list.appendChild(d);
  });
  $('orderTotal').textContent=fmt(o?o.total:0);
}
function esc(s){const d=document.createElement('div');d.textContent=s;return d.innerHTML;}
async function removeItem(item){
  if(!confirm('Remover "'+item.name+'" da comanda?'))return;
  try{
    const r=await sendCommand({type:'remove_item',orderItemId:item.id});
    if(r.status==='applied'||r.status==='success'){toast('Item removido');await loadOrder();}
    else toast(r.message||'Comando rejeitado',true);
  }catch(e){toast(e.message,true);}
}
$('backToTablesBtn').onclick=()=>{state.table=null;loadAreas();show('tablesView');};
$('refreshOrderBtn').onclick=loadOrder;

/* Cardápio */
$('addItemsBtn').onclick=async()=>{
  if(!state.menu.length){try{state.menu=await api('/api/menu');}catch(e){toast(e.message,true);return;}}
  state.cart=[];state.cat=null;state.search='';$('menuSearch').value='';
  $('menuTitle').textContent='Mesa '+state.table.number;
  renderMenu();show('menuView');
};
$('backToOrderBtn').onclick=()=>{state.cart=[];loadOrder();show('tableView');};
$('menuSearch').oninput=e=>{state.search=e.target.value.toLowerCase();renderMenuGrid();};
function renderMenu(){
  const cats=[...new Set(state.menu.map(i=>i.category||'Geral'))];
  if(!state.cat||!cats.includes(state.cat))state.cat=cats[0]||null;
  $('catTabs').innerHTML='';
  cats.forEach(c=>{
    const b=document.createElement('button');b.className='cat-tab'+(c===state.cat?' active':'');b.textContent=c;
    b.onclick=()=>{state.cat=c;renderMenu();};$('catTabs').appendChild(b);
  });
  renderMenuGrid();renderCartBar();
}
function renderMenuGrid(){
  const grid=$('menuGrid');grid.innerHTML='';
  state.menu.filter(i=>(!state.cat||(i.category||'Geral')===state.cat)&&(!state.search||i.name.toLowerCase().includes(state.search)))
    .forEach(i=>{
      const b=document.createElement('button');b.className='menu-item';
      b.innerHTML='<div class="n">'+esc(i.name)+'</div>'+(i.description?'<div>'+esc(i.description)+'</div>':'')+'<div class="p">'+fmt(i.price)+'</div>';
      b.onclick=()=>openItemModal(i);grid.appendChild(b);
    });
}
function renderCartBar(){
  const bar=$('cartBar');
  if(!state.cart.length){bar.classList.add('hidden');return;}
  bar.classList.remove('hidden');
  const total=state.cart.reduce((s,c)=>s+c.quantity*(c.price+c.options.reduce((o,x)=>o+x.extraPrice*x.quantity,0)),0);
  $('sendCartBtn').textContent='Enviar comanda · '+state.cart.reduce((s,c)=>s+c.quantity,0)+' item(ns) · '+fmt(total);
}
$('sendCartBtn').onclick=async()=>{
  const btn=$('sendCartBtn');btn.disabled=true;btn.textContent='Enviando...';
  let ok=0,err=null;
  for(const c of state.cart){
    try{
      const r=await sendCommand({
        type:'add_item',
        menuItemId:c.id,
        quantity:c.quantity,
        observation:c.observation||null,
        options:c.options.map(o=>({id:o.id,optionId:o.id,quantity:o.quantity,extraPrice:o.extraPrice}))
      });
      if(r.status==='applied'||r.status==='success')ok++;else err=r.message;
    }catch(e){err=e.message;}
  }
  btn.disabled=false;
  if(err)toast(err,true);else toast('Comanda enviada para a cozinha! ✓');
  state.cart=[];await loadOrder();show('tableView');
};
async function sendCommand(body){
  body.areaId=state.table.areaId;body.tableId=state.table.id;
  return api('/api/commands',{method:'POST',body:JSON.stringify(body)});
}

/* Modal de item */
let modalItem=null,modalQty=1;
function openItemModal(item){
  modalItem=item;modalQty=1;$('qtyVal').textContent='1';$('modalObs').value='';
  $('modalItemName').textContent=item.name;$('modalItemPrice').textContent=fmt(item.price);
  const box=$('modalOptions');box.innerHTML='';
  if(item.options&&item.options.length){
    const lbl=document.createElement('label');lbl.style.cssText='font-size:13px;color:var(--muted);display:block;margin-bottom:6px';
    lbl.textContent='Variações / adicionais';box.appendChild(lbl);
    item.options.forEach(o=>{
      const row=document.createElement('label');row.className='opt-row';
      row.innerHTML='<input type="checkbox" data-id="'+o.id+'" data-price="'+o.extraPrice+'"><span class="opt-name">'+esc(o.name)+'</span><span class="opt-price">'+(o.extraPrice>0?'+ '+fmt(o.extraPrice):'grátis')+'</span>';
      box.appendChild(row);
    });
  }
  $('itemModal').classList.remove('hidden');
}
$('qtyMinus').onclick=()=>{if(modalQty>1)modalQty--;$('qtyVal').textContent=modalQty;};
$('qtyPlus').onclick=()=>{if(modalQty<99)modalQty++;$('qtyVal').textContent=modalQty;};
$('modalCancel').onclick=()=>$('itemModal').classList.add('hidden');
$('modalAdd').onclick=()=>{
  const opts=[...document.querySelectorAll('#modalOptions input[type=checkbox]:checked')]
    .map(c=>({id:c.dataset.id,quantity:modalQty,extraPrice:parseFloat(c.dataset.price)}));
  state.cart.push({id:modalItem.id,name:modalItem.name,price:modalItem.price,quantity:modalQty,observation:$('modalObs').value.trim(),options:opts});
  $('itemModal').classList.add('hidden');renderCartBar();toast(modalItem.name+' adicionado');
};

/* Boot */
fetch('/api/status').then(r=>r.json()).then(s=>{$('storeName').textContent=s.store+' · rede local';}).catch(()=>{$('storeName').textContent='Modo local';});
if(state.token&&state.name)enterApp();else show('loginView');
</script>
</body>
</html>
""";
}