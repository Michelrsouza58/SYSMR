# Site de pedidos dos garçons — contrato Firebase do SYSMR

## 1. Objetivo e limite desta arquitetura

Este documento descreve como criar um site **somente frontend** para que garçons autenticados consultem o cardápio e as mesas de uma loja SYSMR do tipo Restaurante e enviem pedidos.

O site usa diretamente Firebase Authentication e Realtime Database por meio do Firebase Web SDK. Não há API própria, Cloud Function, servidor web confiável nem Admin SDK. As regras do Realtime Database são a barreira de autorização para cada leitura e gravação.

**Importante:** as regras do Firebase não substituem um backend confiável. Elas controlam quem pode acessar caminhos e validam a estrutura dos dados, mas não executam validações complexas de estoque, preço, estado financeiro ou concorrência de negócio. Uma conta de garçom autorizada pode modificar o site no navegador ou chamar o Firebase SDK diretamente; regras precisam impedir que isso lhe dê poderes além dos pedidos permitidos.

Esta integração exige trabalho também no SYSMR desktop. A versão atual:

- envia seus próprios eventos offline pela fila SQLite para `clients/{uid}/branches/restaurant/events/{eventId}`;
- publica o snapshot do cardápio da loja Restaurante em `clients/{uid}/branches/restaurant/restaurantLive/menu`, sem compartilhar o catálogo com outras lojas;
- publica áreas e mesas em `clients/{uid}/branches/restaurant/restaurantLive/areas`, preservando os IDs estáveis do SQLite e sincronizando mudanças de área/quantidade de mesas;
- desativa áreas sem apagar o histórico; uma área com comanda aberta não pode ser desativada;
- consulta periodicamente os comandos pendentes de `waiterCommands`, valida o garçom/área/mesa/cardápio e importa `addItem` automaticamente para a comanda SQLite;
- grava o recibo do comando na mesma transação local do item/estoque/outbox, para reentregas não duplicarem itens;
- atualiza `status` e `result` do comando no Firebase após o commit local;
- publica `openOrder` dentro da mesa em `restaurantLive/areas`, tanto para pedidos recebidos do site quanto para itens adicionados no desktop;
- cria IDs de mesas localmente, que são estáveis nessa instalação, mas não iguais entre instalações independentes.

Portanto, **não conecte o site ao caminho `events` como se isso, sozinho, atualizasse o PDV**. O contrato a seguir separa comandos dos garçons dos eventos de sincronização desktop e exige um consumidor no desktop. Sem esse consumidor e a publicação de um catálogo compartilhado, o site pode autenticar e gravar comandos no Firebase, mas eles não aparecerão nas comandas locais do SYSMR.

## 2. Decisão de arquitetura

Use o Firebase como transporte e fila de comandos, com o computador SYSMR da loja como consumidor e autoridade local da comanda:

1. O desktop publica o cardápio em `restaurantLive/menu` e as áreas/mesas, incluindo o conteúdo da comanda aberta, em `restaurantLive/areas`.
2. O site lê o cardápio e cada área atribuída ao garçom pelos IDs em `restaurantAccess/{waiterUid}/areas`.
3. O site cria um comando imutável `addItem` em `waiterCommands/{commandId}`.
4. O desktop consulta periodicamente comandos `pending`, valida a identidade do garçom e aplica automaticamente cada `addItem` em uma transação SQLite. A gravação de idempotência, item, eventual baixa de estoque e evento de saída são atômicos.
5. Depois do commit local, o desktop atualiza o comando para `applied`; erros de validação/estoque resultam em `rejected`.
6. O desktop atualiza `openOrder` no snapshot da mesa. O site observa essa projeção e pode mostrar também itens inseridos diretamente no desktop.

O site mostra o pedido como **Pendente de confirmação** até o desktop marcá-lo como aplicado. Se o computador estiver offline, os comandos ficam pendentes no Firebase; não se deve apresentá-los como itens já confirmados na comanda.

Recomenda-se inicialmente **um único desktop SYSMR ativo como escritor/consumidor por loja**. Dois desktops com bancos SQLite independentes não se tornam consistentes apenas por usarem a mesma conta Firebase. Para operação multi-terminal, primeiro será necessária uma estratégia de sincronização bidirecional e resolução de conflitos entre os bancos.

## 3. Caminhos e identificação de loja

Todos os dados operacionais permanecem isolados pelo UID Firebase da conta da loja:

```text
clients/{storeUid}/branches/restaurant/
```

O formulário de login pede o **nome/código de acesso estável da loja**, o **número do garçom** e a **senha do garçom**. O nome/código de acesso da loja precisa ser único no projeto Firebase e não deve mudar quando a loja alterar seu nome de exibição. Não use busca pública em `/clients` para descobrir o UID da loja. Depois que Firebase Authentication autenticar o garçom, o site lê o registro privado `/waiterDirectory/{auth.uid}` para descobrir o `storeUid` atribuído pelo proprietário.

O `storeUid` não é uma senha e conhecê-lo não concede acesso. Cada leitura e escrita continua condicionada pelas regras à identidade autenticada, ao mapeamento do UID e à concessão ativa naquela loja.

Estrutura reservada por este contrato:

```text
waiterDirectory/{waiterUid}           # vínculo privado, leitura do próprio UID/loja
clients/{storeUid}/
  storeType: "restaurant"
  restaurantAccess/{waiterUid}        # perfil operacional e áreas autorizadas
  branches/restaurant/
    events/{eventId}                 # eventos existentes enviados pelo SYSMR
    restaurantLive/
      menu/
        schemaVersion
        updatedAt
        categories/{categoryId}
        items/{menuItemId}
      areas/
        {areaId}/
          name
          sortOrder
          isActive
          tables/{tableId}/
            number
            isActive
            openOrder: null | {
              id, openedAt, employeeName, items[], subtotal, total
              waiterServiceChargeEnabled, waiterServiceCharge, paidTotal, remainingTotal
            }
    waiterCommands/{commandId}
```

`events` continua sendo o caminho de eventos existentes do SYSMR. O site **não** grava em `events`. Os comandos do site têm formato e regras próprios em `waiterCommands`.

Em `restaurantLive/areas`, cada ID de área é filho direto do nó `areas` (por exemplo, `areas/{areaId}/name`). Não há um segundo filho chamado `areas` nem metadados `schemaVersion`/`updatedAt` nesse nó; o desktop substitui o mapa de áreas pelo snapshot atual.

## 4. Autenticação e concessão de acesso

### 4.1 Login com loja + número do garçom + senha

Firebase Authentication Email/Password exige um endereço de e-mail; ele não oferece login nativo por nome de loja e número de funcionário. Para manter o formulário solicitado sem implementar backend, derive um **e-mail técnico determinístico** dos campos de loja e garçom:

```text
waiterEmail = "garcom." + storeSlug + "." + waiterNumber + "@login.sysmr.invalid"
```

No desktop SYSMR, o `storeSlug` mostrado ao Super Admin é o número/código permanente da loja normalizado para minúsculas. Exemplo: código de loja `LOJA-001`, garçom `0042` resulta em `garcom.loja-001.0042@login.sysmr.invalid`. O domínio `.invalid` é intencional: não é endereço de e-mail de recuperação e não deve receber mensagens.

Regras de canonicalização, que devem ser implementadas identicamente no cadastro e no login:

- `storeSlug`: no cadastro existente do desktop é o número/código permanente da loja em minúsculas, somente `[a-z0-9-]`, começando e terminando com letra/número e com 3–32 caracteres. Não use o nome comercial, que pode mudar.
- `waiterNumber`: identificador alfanumérico atribuído pela loja, maiúsculo, somente `[A-Z0-9-]`, entre 1 e 20 caracteres. Preserve zeros à esquerda: `0042` é diferente de `42`.
- Antes de compor o e-mail, validar ambos os valores. Não interpolar texto arbitrário no endereço.
- A senha é a senha pessoal do garçom e é cadastrada em Firebase Authentication. Nunca a grave em RTDB, em `localStorage`, no banco SQLite ou em logs.

Fluxo do login:

1. O garçom informa `storeSlug`, `waiterNumber` e senha.
2. O frontend deriva `waiterEmail` e chama `signInWithEmailAndPassword(auth, waiterEmail, password)`.
3. Use o UID retornado por Firebase Authentication como identidade; UID não é digitado nem escolhido pelo usuário.
4. Leia `waiterDirectory/{uid}`. Confira `active === true`, `waiterUid === uid`, o `storeSlug` e o `waiterNumber` informados. Leia `clients/{storeUid}/restaurantAccess/{uid}` e confirme que está ativo, que o número coincide e que o papel é permitido. Se qualquer perfil não existir ou discordar, faça `signOut` e negue acesso.
5. Confira `clients/{storeUid}/storeType === "restaurant"` antes de abrir o sistema.
6. Limpe o campo de senha imediatamente após sucesso ou falha. Configure persistência de sessão adequada ao dispositivo; em tablet compartilhado prefira `browserSessionPersistence` e disponibilize sair da conta.

Exemplo de login:

```javascript
import { signInWithEmailAndPassword, setPersistence, browserSessionPersistence, signOut } from "firebase/auth";
import { get, ref } from "firebase/database";

function normalizeStoreSlug(value) {
  const slug = value.trim().toLowerCase();
  if (!/^[a-z0-9][a-z0-9-]{1,38}[a-z0-9]$/.test(slug)) {
    throw new Error("Nome/código da loja inválido.");
  }
  return slug;
}

function normalizeWaiterNumber(value) {
  const number = value.trim().toUpperCase();
  if (!/^[A-Z0-9-]{1,20}$/.test(number)) {
    throw new Error("Número do garçom inválido.");
  }
  return number;
}

export async function signInWaiter(auth, db, storeValue, numberValue, password) {
  const storeSlug = normalizeStoreSlug(storeValue);
  const waiterNumber = normalizeWaiterNumber(numberValue);
  const email = `garcom.${storeSlug}.${waiterNumber}@login.sysmr.invalid`;

  await setPersistence(auth, browserSessionPersistence);
  const credential = await signInWithEmailAndPassword(auth, email, password);
  const uid = credential.user.uid;

  try {
    const directorySnapshot = await get(ref(db, `waiterDirectory/${uid}`));
    if (!directorySnapshot.exists()) throw new Error("Garçom não cadastrado para uma loja.");
    const directory = directorySnapshot.val();
    if (!directory.active || directory.waiterUid !== uid ||
        directory.storeSlug !== storeSlug || directory.waiterNumber !== waiterNumber ||
        typeof directory.storeUid !== "string" || directory.storeUid.length === 0) {
      throw new Error("Acesso do garçom desativado ou incompatível.");
    }

    const accessSnapshot = await get(ref(
      db, `clients/${directory.storeUid}/restaurantAccess/${uid}`
    ));
    if (!accessSnapshot.exists()) {
      throw new Error("Acesso à loja não autorizado.");
    }
    const access = accessSnapshot.val();
    if (access.active !== true || access.waiterNumber !== waiterNumber ||
        !["waiter", "manager"].includes(access.role)) {
      throw new Error("Acesso à loja não autorizado.");
    }
    const storeTypeSnapshot = await get(ref(
      db, `clients/${directory.storeUid}/storeType`
    ));
    if (storeTypeSnapshot.val() !== "restaurant") {
      throw new Error("A loja não está configurada como restaurante.");
    }

    return { user: credential.user, directory, access };
  } catch (error) {
    await signOut(auth);
    throw error;
  }
}
```

Mensagens de erro de login devem ser genéricas para não revelar se um número de garçom está cadastrado. Registre erros técnicos sem senha, ID token ou refresh token.

### 4.2 Cadastro de conta e número do garçom sem backend

Firebase Authentication Email/Password não possui criação segura de contas subordinadas pelo dono da loja no SDK web. Não há API cliente para o UID do proprietário criar arbitrariamente uma senha em outra conta. Com arquitetura estritamente frontend-only, há duas opções:

**Desktop SYSMR:** a tela Funcionários cria a conta de Authentication com Email/Password usando o e-mail técnico derivado e a senha inicial informada. Em seguida, grava o UID e os metadados em `waiterDirectory/{waiterUid}` e `clients/{storeUid}/restaurantAccess/{waiterUid}` por uma atualização multipath do RTDB, e guarda apenas o UID, número e áreas no SQLite. O desktop não tenta ler previamente `waiterDirectory/{waiterUid}`: as regras permitem ao dono criar o próprio vínculo, mas não ler um caminho de UID que ainda não existe; a validação de escrita protege a criação e impede que outro dono altere um vínculo existente. Se o processo for interrompido depois da criação Auth, repetir o cadastro com o mesmo número e senha inicial recupera a conta criada e conclui o vínculo; não reutilize a senha em outro garçom.

**Provisionamento web futuro:** uma implementação JavaScript pode usar uma segunda instância Firebase App/Auth isolada para chamar `createUserWithEmailAndPassword`, sem encerrar a sessão administrativa da loja.

Exemplo esquemático da segunda instância:

```javascript
import { initializeApp, deleteApp } from "firebase/app";
import { createUserWithEmailAndPassword, getAuth, signOut } from "firebase/auth";
import { ref, set } from "firebase/database";

async function createWaiterAccount(firebaseConfig, primaryAuth, primaryDb, store, waiter) {
  if (primaryAuth.currentUser?.uid !== store.storeUid) {
    throw new Error("Somente a conta proprietária pode cadastrar garçons.");
  }
  const storeSlug = normalizeStoreSlug(store.storeSlug);
  const waiterNumber = normalizeWaiterNumber(waiter.number);
  const waiterEmail = `garcom.${storeSlug}.${waiterNumber}@login.sysmr.invalid`;
  const secondaryApp = initializeApp(firebaseConfig, `waiter-create-${crypto.randomUUID()}`);
  const secondaryAuth = getAuth(secondaryApp);

  try {
    const credential = await createUserWithEmailAndPassword(
      secondaryAuth, waiterEmail, waiter.initialPassword
    );
    const waiterUid = credential.user.uid;
    await signOut(secondaryAuth);

    const now = new Date().toISOString();
    const directory = {
      waiterUid,
      storeUid: store.storeUid,
      storeSlug,
      storeName: store.displayName,
      waiterNumber,
      active: true,
      createdAt: now
    };
    const access = {
      active: true,
      displayName: waiter.name,
      waiterNumber,
      role: "waiter",
      employeeId: waiter.employeeId ?? "",
      areas: waiter.areaIds.reduce((map, id) => ({ ...map, [id]: true }), {})
    };

    await set(ref(primaryDb, `waiterDirectory/${waiterUid}`), directory);
    await set(ref(
      primaryDb, `clients/${store.storeUid}/restaurantAccess/${waiterUid}`
    ), access);
    return waiterUid;
  } finally {
    await signOut(secondaryAuth).catch(() => {});
    await deleteApp(secondaryApp);
  }
}
```

O exemplo precisa de tratamento de falha parcial na aplicação real: se a gravação no primeiro caminho funcionar e a segunda falhar, mostre erro explícito e permita retomar com o mesmo UID; não crie outra identidade nem declare cadastro completo. As regras não oferecem transação atômica entre dois caminhos globais diferentes e a criação de Auth não pode participar de uma transação RTDB. O próprio cliente pode chamar o endpoint público de cadastro do Firebase Auth; isso pode gerar contas órfãs, mas sem perfil e concessão elas não recebem acesso aos dados. Configure proteção contra abuso e monitore usuários órfãos.

Como o e-mail termina em `.invalid`, o fluxo padrão de redefinição por e-mail não funciona. O site deve oferecer troca de senha para o próprio usuário autenticado (`updatePassword`, com reautenticação quando exigida). Se o garçom esquecer a senha, a recuperação exige procedimento administrativo seguro no Firebase Console ou, futuramente, um backend confiável; não exponha credenciais de Admin SDK no site.

O número é único dentro da loja por derivar o e-mail técnico: Firebase Auth rejeita e-mails duplicados. Não é possível impor essa unicidade por regra RTDB em uma consulta global; o formulário deve tratar `EMAIL_EXISTS` como conflito e não criar uma segunda concessão.

### 4.3 Perfil operacional e autorização por loja

O `storeSlug` também precisa ser único no projeto Firebase. Sem backend confiável, a unicidade deve ser atribuída e controlada pelo processo administrativo de provisionamento; as regras abaixo validam o formato, mas não reservam slugs globalmente. Não permita que o cliente escolha ou altere livremente esse identificador. Depois do provisionamento, `storeSlug` e `waiterNumber` são imutáveis nas regras porque compõem o e-mail de autenticação; uma mudança de identificador requer um fluxo explícito de migração da conta Auth, não apenas editar o perfil RTDB.

#### Mapeamento do funcionário para a loja

Uma pessoa responsável pela loja deve cadastrar uma concessão por funcionário em:

```text
clients/{storeUid}/restaurantAccess/{waiterUid}
```

Exemplo:

```json
{
  "active": true,
  "displayName": "Ana",
  "waiterNumber": "0042",
  "role": "waiter",
  "employeeId": "GUID-DO-FUNCIONARIO-LOCAL",
  "areas": {
    "GUID-DA-AREA-SALAO": true
  }
}
```

Campos:

- `active`: booleano; deve ser `true` para usar o site.
- `displayName`: nome a mostrar na interface e auditoria visual.
- `waiterNumber`: número de login do garçom, texto para preservar zeros à esquerda. Ele não é senha.
- `role`: as contas criadas pelo SYSMR desktop usam `waiter`. O papel `manager` mostrado em partes da proposta é uma extensão futura e não é provisionado pelo aplicativo atual.
- `employeeId`: GUID do funcionário local do Restaurante, publicado pelo desktop. O UID do Firebase é uma identidade diferente e não deve ser usado como ID local.
- `areas`: mapa de IDs de áreas atribuídas. O garçom só pode consultar as áreas marcadas como `true` e enviar comandos para elas.

No contrato inicial, `waiter` pode criar comandos `addItem`; a proposta permite ao papel futuro `manager` somente leitura para supervisão. O SYSMR desktop atual não provisiona esse papel. Se ele for adicionado, altere explicitamente o fluxo de cadastro e teste as permissões antes de publicar as regras.

Além da concessão, o UID autenticado precisa de um mapeamento em:

```text
waiterDirectory/{waiterUid}
```

Exemplo:

```json
{
  "waiterUid": "FIREBASE-AUTH-UID-DO-GARCOM",
  "storeUid": "FIREBASE-AUTH-UID-DA-LOJA",
  "storeSlug": "loja-001",
  "storeName": "Cantina Central",
  "waiterNumber": "0042",
  "employeeId": "GUID-DO-FUNCIONARIO-LOCAL",
  "active": true,
  "createdAt": "2026-10-12T23:00:00.000Z"
}
```

Esse registro permite ao frontend, após autenticar, descobrir a qual loja pertence o UID do garçom sem uma leitura pública de todas as lojas. Só o UID da loja pode criar/alterar/excluir o vínculo. O próprio UID do garçom pode ler somente o seu registro. Não há campo de senha nesse JSON: senha fica exclusivamente no Firebase Authentication.

A concessão e o registro de diretório devem ser consistentes. Faça as duas gravações pelo painel autenticado como a loja e só mostre o funcionário como cadastrado após ambas serem confirmadas. O cadastro a partir do desktop exige configuração Firebase válida e uma sessão de loja com refresh token (faça um login online pelo menos uma vez). O desktop mostra o código de loja e o número do garçom após concluir.

Remover acesso significa definir `active` como `false` em ambos os registros ou remover a concessão. A revogação impede novas leituras/gravações após reavaliação das regras; encerre também a sessão do funcionário no site. As regras preservam `storeSlug` e `waiterNumber` em atualizações do diretório para evitar divergência com o e-mail Auth.

## 5. Projeção compartilhada de leitura

O desktop deve publicar uma projeção para consulta do site. Um formato recomendado:

```text
restaurantLive/
  menu:
    schemaVersion: 1
    updatedAt: "2026-10-12T23:00:00.0000000+00:00"
    categories/{categoryId}:
      id: "{categoryId}"
      name: "Bebidas"
    items/{menuItemId}:
      id: "{menuItemId}"
      name: "Suco de laranja"
      categoryId: "{categoryId}"
      price: 12.50
      isActive: true
  areas/{areaId}:
    id: "GUID-DA-AREA"
    name: "Salão"
    sortOrder: 0
    isActive: true
    tables/{tableId}:
      number: 1
      isActive: true
      openOrder: null | {
        id: "GUID-DA-COMANDA",
        openedAt: "2026-10-12T23:00:00.000Z",
        employeeName: "Ana",
        items: [{
          id: "GUID-DO-ITEM-NA-COMANDA",
          name: "Suco de laranja",
          quantity: 2,
          unitPrice: 12.5,
          observation: "Sem açúcar"
        }],
        subtotal: 25.0,
        waiterServiceChargeEnabled: false,
        waiterServiceCharge: 0,
        total: 25.0,
        paidTotal: 0,
        remainingTotal: 25.0
      }
```

O desktop publica o snapshot completo do cardápio em `clients/{storeUid}/branches/restaurant/restaurantLive/menu` após login da loja Restaurante e quando o cardápio é alterado. O `storeUid` é a chave de escopo: cada loja tem seu próprio nó e não compartilha itens com outras lojas. O nó contém `schemaVersion` e `updatedAt`, mesmo quando ainda não há itens, para que o catálogo vazio também tenha uma representação explícita. Itens e categorias são substituídos como um snapshot; itens inativos permanecem publicados com `isActive: false`, e o site não deve oferecê-los em novos pedidos.

O desktop publica também um snapshot de `restaurantLive/areas` no login e sempre que uma área ou quantidade de mesas é alterada. A estrutura é `areas/{areaId}` com `id`, `name`, `sortOrder`, `isActive` e `tables/{tableId}` com `number` e `isActive`. O mapa `restaurantAccess/{waiterUid}/areas` usa os mesmos GUIDs de área como chaves. O desktop só oferece áreas ativas para atribuição ao garçom; a regra autoriza leitura do nó de uma área somente quando a loja está ativa, o perfil do garçom está ativo, a área está atribuída e `isActive === true`. Leia os nós de área por ID, não faça uma leitura ampla de `/areas`, pois as regras não filtram listas.

Cada mesa inclui `openOrder`: `null` quando está livre ou um snapshot com atendente, itens e totais quando há comanda aberta. O desktop república esse mapa depois de mudanças de comanda (inclusive alterações locais no desktop), fechamento/pagamento e importação de pedido do site. O app web deve usar esse snapshot para exibir o mesmo estado que o PDV; não trate `waiterCommands` como histórico completo de consumo.

Desativar uma área é uma operação reversível, não exclusão física. Ela e suas mesas ficam no snapshot com `isActive: false`, preservando IDs e referências históricas. O desktop bloqueia a desativação enquanto houver comanda aberta na área, mantém pelo menos uma área ativa e pode reativar a área com os mesmos IDs.

O SYSMR atual oferece cinco modelos no dropdown do Super Admin para inserir itens de exemplo: `pesqueiro`, `churrascaria`, `pizzaria`, `lanchonete-hamburgueria` e `restaurante-caseiro`. O modelo selecionado popula o cardápio local e os itens são publicados na projeção; o tipo escolhido ainda não é persistido como metadado separado da loja. O site pode agrupar pelo cardápio publicado, mas não deve inferir uma vertente exclusiva a partir dos itens, pois o lojista pode editar ou combinar modelos.

As categorias atuais são textos editados no desktop. O desktop publica um registro por categoria e cada item aponta para `categoryId`; esse ID é determinístico a partir do nome normalizado da categoria. O site agrupa o catálogo por `menu/categories` e filtra itens `isActive === true`. O modelo desktop atual oferece categorias de um nível, sem subcategorias ou ordenação manual. IDs de itens são os GUIDs persistentes do SQLite; não gere novos IDs no navegador.

Os itens de exemplo dos modelos são sementes editáveis, não um cardápio definitivo: o estabelecimento deve revisar nomes e preços antes de ativar/publicar as vendas. A projeção não inclui código de barras, custo ou vínculo com estoque.

Use sempre IDs persistentes e reais do SQLite para áreas, mesas, itens de cardápio e comandas. O número visível da mesa não é chave suficiente: “Mesa 1” pode existir em várias áreas. O site não deve inventar IDs nem gerar uma cópia do cardápio ou das mesas.

O desktop cria as mesas localmente e publica a correspondência completa `{areaId, tableId, number, isActive}` junto com as áreas. Mantenha esses IDs estáveis quando uma mesa for reativada.

### Leitura pelo site

- Leia `restaurantAccess/{auth.uid}` para saber o nome, papel e áreas atribuídas ao usuário.
- Leia `restaurantLive/menu` para exibir o cardápio agrupado por categoria.
- Para cada chave marcada em `restaurantAccess/{auth.uid}/areas`, leia diretamente `restaurantLive/areas/{areaId}`. Ignore a área e a mesa se estiverem inativas. Não tente ler a coleção `restaurantLive/areas` inteira como garçom: a regra concede acesso a cada área atribuída, não a uma listagem geral.
- Leia cada `restaurantLive/areas/{areaId}` somente para uma área presente na concessão.
- Inscreva-se com `onValue` ou `onChildAdded`/`onChildChanged` do SDK para acompanhar alterações.
- Nunca conclua que uma mesa está livre com base em um dado antigo mantido só em memória. Releia a projeção antes de oferecer o envio e deixe o desktop validar novamente.
- Não use um listener no caminho `clients` inteiro. Leia apenas os caminhos necessários.

## 6. Comando de pedido enviado pelo site

O site começa com um único tipo de comando suportado: `addItem`.

Exemplo em:

```text
clients/{storeUid}/branches/restaurant/waiterCommands/{commandId}
```

```json
{
  "id": "9cc0372a-e77d-4762-8cb2-90b92bc1c1bd",
  "type": "addItem",
  "employeeUid": "UID-AUTENTICADO-DO-GARCOM",
  "areaId": "GUID-DA-AREA",
  "tableId": "GUID-DA-MESA",
  "menuItemId": "GUID-DO-ITEM-DO-CARDAPIO",
  "quantity": 2,
  "observation": "Sem cebola e molho à parte",
  "createdAt": "2026-10-12T22:10:00.000Z",
  "status": "pending"
}
```

Regras do contrato:

- `commandId` é um UUID criado uma única vez para a ação e é igual a `id`.
- Reutilize o mesmo ID quando repetir uma tentativa cujo resultado de rede ficou incerto. Nunca crie um novo comando automaticamente só porque ocorreu timeout.
- `employeeUid` deve ser exatamente o UID autenticado.
- `areaId`, `tableId` e `menuItemId` vêm do catálogo publicado pelo desktop.
- `quantity` precisa ser número maior que zero e limitada pela interface (por exemplo, no máximo 99 por item).
- `observation` é opcional; quando preenchida deve ser texto de até 250 caracteres. Omita a propriedade quando não houver observação; não envie `null`.
- O site não envia nome, categoria, preço, total, estoque, status de pagamento nem `orderId` como autoridade. O desktop consulta esses valores localmente e encontra/cria a comanda aberta pela mesa.
- O site só cria registros com `status: "pending"`. Só o desktop altera o status e acrescenta resultado.
- Use um ID novo para uma nova ação intencional, mesmo que seja o mesmo produto/mesa; use o ID anterior para uma repetição técnica da mesma ação.
- `addItem` não cancela ou remove item e não recebe pagamento. Essas operações precisam de comandos próprios, autorização e validação, e não devem ser simuladas alterando o comando existente.

### Exemplo de gravação no Firebase Web SDK modular

Código ilustrativo; `db`, `storeUid` e `user` devem vir da inicialização/autenticação do aplicativo:

```javascript
import { ref, get, set } from "firebase/database";

export async function submitAddItem(db, storeUid, user, areaId, tableId, menuItemId, quantity, observation = "") {
  if (!Number.isFinite(quantity) || quantity <= 0 || quantity > 99) {
    throw new Error("Quantidade inválida.");
  }
  if (observation.length > 250) {
    throw new Error("A observação deve ter no máximo 250 caracteres.");
  }

  const operationId = crypto.randomUUID();
  const commandRef = ref(
    db,
    `clients/${storeUid}/branches/restaurant/waiterCommands/${operationId}`
  );
  const command = {
    id: operationId,
    type: "addItem",
    employeeUid: user.uid,
    areaId,
    tableId,
    menuItemId,
    quantity,
    ...(observation.trim() ? { observation: observation.trim() } : {}),
    createdAt: new Date().toISOString(),
    status: "pending"
  };

  await set(commandRef, command);
  return operationId;
}

export async function checkCommand(db, storeUid, operationId) {
  const snapshot = await get(ref(
    db,
    `clients/${storeUid}/branches/restaurant/waiterCommands/${operationId}`
  ));
  return snapshot.exists() ? snapshot.val() : null;
}
```

Na prática, persista o `operationId` e os dados da tentativa localmente até conhecer o resultado. Se `set` terminar com timeout/erro de conexão, consulte o mesmo caminho:

- se o comando existe e seu `id`, `employeeUid` e conteúdo correspondem à tentativa, acompanhe aquele comando;
- se não existe, tente gravá-lo novamente com o mesmo ID;
- se existe conteúdo diferente, pare e apresente um erro; não sobrescreva;
- se as regras responderem `PERMISSION_DENIED`, não gere outro ID. O usuário pode estar sem acesso, a área pode não estar atribuída, ou o comando já pode existir.

O `.write` para garçons é de criação; as regras não aceitam sobrescrita do comando nem reenvio cego de `set` para uma chave existente. O desktop deve processar com deduplicação por `commandId` para que comandos nunca sejam aplicados duas vezes.

## 7. Ciclo de processamento no desktop

O desktop consulta a cada cinco segundos a fila `waiterCommands` filtrada por `status === "pending"` (o nó precisa do índice RTDB `"status"`). Para cada comando:

1. Confere tipo `addItem`, UID de funcionário cadastrado/ativo, área atribuída localmente, mesa ativa pertencente à área, item ativo no cardápio e quantidade positiva.
2. Usa o preço e o vínculo de estoque do cadastro local; não confia em preço ou estoque enviado pelo navegador.
3. Cria ou reutiliza a comanda aberta da mesa e inclui o item. A tabela SQLite `RestaurantWaiterCommands` usa `CommandId` como chave única; o recibo, item, eventual baixa de estoque e eventos da outbox são gravados na mesma transação.
4. Atualiza o comando Firebase para `applied` com `result.orderId` ou `rejected` com `result.message`, sempre acrescentando `result.processedAt`.
5. Publica `openOrder` atualizado em `restaurantLive/areas/{areaId}/tables/{tableId}`. Se o desktop reiniciar ou a gravação do recibo no Firebase falhar após o commit, o comando é consultado novamente e a inbox impede duplicação.

Sem internet, o desktop não recebe os pedidos e o site deve deixá-los como pendentes. Ao reconectar, o polling retoma a fila. O SYSMR mantém um único desktop como autoridade local por loja; vários bancos SQLite independentes não são sincronizados entre si por esse fluxo.

## 8. Regras completas do Realtime Database

O JSON abaixo é uma **proposta de substituição/mesclagem** para `SYSMR.FirebaseFunctions/database.rules.json`. Faça backup das regras em produção e valide no Firebase Rules Playground/emulador antes de publicar. Regras RTDB são grants hierárquicos: uma permissão `.read`/`.write` em um pai concede acesso aos descendentes e não pode ser revogada em um filho. Por isso, não adicione `.write` para garçons em `clients/{storeUid}` nem em `branches/restaurant`; conceda somente nos caminhos específicos indicados.

As regras existentes de `events` continuam limitadas ao dono da loja. As regras novas permitem ao garçom ler o próprio cadastro e as áreas atribuídas, ler os pedidos próprios e **criar somente comandos pendentes de inclusão de item**. O dono da loja é o desktop autenticado com o UID `storeUid`.

```json
{
  "rules": {
    "waiterDirectory": {
      "$waiterUid": {
        ".read": "auth != null && (auth.uid === $waiterUid || auth.uid === data.child('storeUid').val())",
        ".write": "auth != null && ((!data.exists() && auth.uid === newData.child('storeUid').val() && newData.child('waiterUid').val() === $waiterUid) || (data.exists() && auth.uid === data.child('storeUid').val() && newData.child('storeUid').val() === data.child('storeUid').val() && newData.child('storeSlug').val() === data.child('storeSlug').val() && newData.child('waiterNumber').val() === data.child('waiterNumber').val() && newData.child('waiterUid').val() === $waiterUid && newData.child('employeeId').val() === data.child('employeeId').val() && newData.child('createdAt').val() === data.child('createdAt').val()))",
        ".validate": "newData.hasChildren(['waiterUid', 'storeUid', 'storeSlug', 'storeName', 'waiterNumber', 'employeeId', 'active', 'createdAt'])",
        "waiterUid": {
          ".validate": "newData.isString() && newData.val() === $waiterUid"
        },
        "storeUid": {
          ".validate": "newData.isString() && newData.val().length > 0"
        },
        "storeSlug": {
          ".validate": "newData.isString() && newData.val().matches(/^[a-z0-9][a-z0-9-]{1,38}[a-z0-9]$/)"
        },
        "storeName": {
          ".validate": "newData.isString() && newData.val().length > 0 && newData.val().length <= 100"
        },
        "waiterNumber": {
          ".validate": "newData.isString() && newData.val().matches(/^[A-Z0-9-]{1,20}$/)"
        },
        "employeeId": {
          ".validate": "newData.isString() && newData.val().length > 0 && newData.val().length <= 80"
        },
        "active": {
          ".validate": "newData.isBoolean()"
        },
        "createdAt": {
          ".validate": "newData.isString() && newData.val().length <= 40"
        },
        "$other": {
          ".validate": "$other === 'waiterUid' || $other === 'storeUid' || $other === 'storeSlug' || $other === 'storeName' || $other === 'waiterNumber' || $other === 'employeeId' || $other === 'active' || $other === 'createdAt'"
        }
      }
    },
    "clients": {
      "$storeUid": {
        ".read": "auth != null && auth.uid === $storeUid",
        "storeType": {
          ".read": "auth != null && (auth.uid === $storeUid || (root.child('waiterDirectory').child(auth.uid).child('storeUid').val() === $storeUid && root.child('waiterDirectory').child(auth.uid).child('active').val() === true && root.child('clients').child($storeUid).child('restaurantAccess').child(auth.uid).child('active').val() === true))",
          ".write": "auth != null && auth.uid === $storeUid",
          ".validate": "newData.val() === 'sales' || newData.val() === 'restaurant'"
        },
        "restaurantAccess": {
          "$waiterUid": {
            ".read": "auth != null && (auth.uid === $storeUid || (auth.uid === $waiterUid && root.child('waiterDirectory').child(auth.uid).child('storeUid').val() === $storeUid))",
            ".write": "auth != null && auth.uid === $storeUid && root.child('clients').child($storeUid).child('storeType').val() === 'restaurant'",
            ".validate": "newData.hasChildren(['active', 'displayName', 'waiterNumber', 'role', 'areas'])",
            "active": {
              ".validate": "newData.isBoolean()"
            },
            "displayName": {
              ".validate": "newData.isString() && newData.val().length > 0 && newData.val().length <= 80"
            },
            "waiterNumber": {
              ".validate": "newData.isString() && newData.val().matches(/^[A-Z0-9-]{1,20}$/)"
            },
            "role": {
              ".validate": "newData.val() === 'waiter'"
            },
            "employeeId": {
              ".validate": "newData.isString() && newData.val().length <= 80"
            },
            "areas": {
              ".validate": "newData.hasChildren()",
              "$areaId": {
                ".validate": "newData.val() === true"
              }
            },
            "$other": {
              ".validate": "$other === 'active' || $other === 'displayName' || $other === 'waiterNumber' || $other === 'role' || $other === 'employeeId' || $other === 'areas'"
            }
          }
        },
        "branches": {
          "$branch": {
            ".read": "auth != null && auth.uid === $storeUid && root.child('clients').child($storeUid).child('storeType').val() === $branch",
            "events": {
              "$eventId": {
                ".write": "auth != null && auth.uid === $storeUid && root.child('clients').child($storeUid).child('storeType').val() === $branch && ($branch === 'sales' || $branch === 'restaurant')",
                ".validate": "newData.hasChildren(['id', 'entity', 'entityId', 'createdAt', 'payload']) && newData.child('id').val() === $eventId"
              }
            },
            "restaurantLive": {
              ".write": "auth != null && auth.uid === $storeUid && root.child('clients').child($storeUid).child('storeType').val() === 'restaurant'",
              "menu": {
                ".read": "auth != null && root.child('clients').child($storeUid).child('storeType').val() === 'restaurant' && (auth.uid === $storeUid || (root.child('waiterDirectory').child(auth.uid).child('storeUid').val() === $storeUid && root.child('waiterDirectory').child(auth.uid).child('active').val() === true && root.child('clients').child($storeUid).child('restaurantAccess').child(auth.uid).child('active').val() === true && (root.child('clients').child($storeUid).child('restaurantAccess').child(auth.uid).child('role').val() === 'waiter' || root.child('clients').child($storeUid).child('restaurantAccess').child(auth.uid).child('role').val() === 'manager')))"
              },
              "areas": {
                "$areaId": {
                  ".read": "auth != null && root.child('clients').child($storeUid).child('storeType').val() === 'restaurant' && data.child('isActive').val() === true && (auth.uid === $storeUid || (root.child('waiterDirectory').child(auth.uid).child('storeUid').val() === $storeUid && root.child('waiterDirectory').child(auth.uid).child('active').val() === true && root.child('clients').child($storeUid).child('restaurantAccess').child(auth.uid).child('active').val() === true && root.child('clients').child($storeUid).child('restaurantAccess').child(auth.uid).child('areas').child($areaId).val() === true))"
                }
              }
            },
            "waiterCommands": {
              ".indexOn": ["status"],
              "$commandId": {
                ".read": "auth != null && (auth.uid === $storeUid || (auth.uid === data.child('employeeUid').val() && root.child('waiterDirectory').child(auth.uid).child('storeUid').val() === $storeUid && root.child('waiterDirectory').child(auth.uid).child('active').val() === true && root.child('clients').child($storeUid).child('restaurantAccess').child(auth.uid).child('active').val() === true && root.child('clients').child($storeUid).child('restaurantAccess').child(auth.uid).child('areas').child(data.child('areaId').val()).val() === true))",
                ".write": "auth != null && ((auth.uid === $storeUid && root.child('clients').child($storeUid).child('storeType').val() === 'restaurant' && $branch === 'restaurant') || (!data.exists() && newData.child('status').val() === 'pending' && !newData.child('result').exists() && auth.uid === newData.child('employeeUid').val() && root.child('waiterDirectory').child(auth.uid).child('storeUid').val() === $storeUid && root.child('waiterDirectory').child(auth.uid).child('active').val() === true && root.child('clients').child($storeUid).child('storeType').val() === 'restaurant' && root.child('clients').child($storeUid).child('restaurantAccess').child(auth.uid).child('active').val() === true && root.child('clients').child($storeUid).child('restaurantAccess').child(auth.uid).child('role').val() === 'waiter' && root.child('clients').child($storeUid).child('restaurantAccess').child(auth.uid).child('areas').child(newData.child('areaId').val()).val() === true && root.child('clients').child($storeUid).child('branches').child('restaurant').child('restaurantLive').child('areas').child(newData.child('areaId').val()).child('tables').child(newData.child('tableId').val()).child('isActive').val() === true && root.child('clients').child($storeUid).child('branches').child('restaurant').child('restaurantLive').child('menu').child('items').child(newData.child('menuItemId').val()).child('isActive').val() === true))",
                ".validate": "newData.hasChildren(['id', 'type', 'areaId', 'tableId', 'employeeUid', 'menuItemId', 'quantity', 'status', 'createdAt']) && newData.child('id').val() === $commandId && newData.child('type').val() === 'addItem'",
                "id": {
                  ".validate": "newData.isString() && newData.val() === $commandId"
                },
                "type": {
                  ".validate": "newData.val() === 'addItem'"
                },
                "employeeUid": {
                  ".validate": "newData.isString() && newData.val().length > 0"
                },
                "areaId": {
                  ".validate": "newData.isString() && newData.val().length > 0"
                },
                "tableId": {
                  ".validate": "newData.isString() && newData.val().length > 0"
                },
                "menuItemId": {
                  ".validate": "newData.isString() && newData.val().length > 0"
                },
                "quantity": {
                  ".validate": "newData.isNumber() && newData.val() > 0 && newData.val() <= 99"
                },
                "createdAt": {
                  ".validate": "newData.isString() && newData.val().length > 0"
                },
                "observation": {
                  ".validate": "newData.isString() && newData.val().length <= 250"
                },
                "status": {
                  ".validate": "newData.val() === 'pending' || newData.val() === 'applied' || newData.val() === 'rejected'"
                },
                "result": {
                  "message": {
                    ".validate": "newData.isString()"
                  },
                  "orderId": {
                    ".validate": "newData.isString()"
                  },
                  "processedAt": {
                    ".validate": "newData.isString()"
                  },
                  "$other": {
                    ".validate": "$other === 'message' || $other === 'orderId' || $other === 'processedAt'"
                  }
                },
                "$other": {
                  ".validate": "$other === 'id' || $other === 'type' || $other === 'areaId' || $other === 'tableId' || $other === 'employeeUid' || $other === 'menuItemId' || $other === 'quantity' || $other === 'status' || $other === 'createdAt' || $other === 'observation' || $other === 'result'"
                }
              }
            }
          }
        }
      }
    }
  }
}
```

### Notas sobre as regras

- O UID do dono é o único que pode criar concessões e publicar `restaurantLive`.
- O garçom não pode ler o ramo inteiro nem a fila inteira: pode ler individualmente o comando que enviou e que pertence a uma área ainda atribuída. O desktop (UID da loja) consulta a fila por `status == "pending"`.
- O site só pode criar comandos quando `active == true`, `role == "waiter"` e a área/mesa/item estão ativos e atribuídos.
- O site não pode escrever o catálogo, mesa, comanda, caixa, pagamento nem eventos.
- A leitura de áreas é por caminho `areas/{areaId}`: o garçom só recebe acesso à área atribuída e ativa; não faça `get()` em toda a coleção de áreas. O dono pode ler e publicar o snapshot completo.
- `.write` em um nó permite alteração naquele nó inteiro; as regras restringem os garçons a novo registro pendente (`!data.exists()`). Escritas posteriores de resultado devem vir do desktop autenticado como o dono.
- O desktop pode alterar comandos e a projeção, pois autentica como `$storeUid`. Proteja a conta da loja e não use a senha dela no site.
- `.validate` não roda para remoção de dados. O site não recebe permissão de apagar comandos; o desktop deve arquivar ou manter recibos, e remoções administrativas devem ser feitas com cuidado.
- As regras não podem confirmar que `tableId` pertence ao `areaId`, que o item de cardápio existe, que a mesa continua aberta, nem que há estoque suficiente. O desktop precisa verificar tudo isso antes do commit SQLite.
- O formato inicial aceita somente comandos do tipo `addItem`; ele **não** limita a quantidade de comandos que um UID pode enviar dentro de uma área autorizada. Configure cotas/monitoramento no projeto e limite também quantidade/tamanho no cliente.
- A regra de `restaurantAccess` aceita apenas os campos listados. `employeeId` é opcional. O mapa `areas` deve conter IDs verdadeiros da publicação do desktop.
- A validação e consulta de `restaurantLive/areas` dá leitura da área atribuída inteira. Não coloque dados fiscais, credenciais ou dados sensíveis de cliente nessa projeção.

Antes de deploy, valide as regras no simulador com todos estes casos: usuário anônimo; UID de outra loja; funcionário inativo; funcionário ativo sem área; funcionário ativo na área atribuída; funcionário tentando escrever `payment`, `removeItem`, preço ou status `applied`; tentativa de sobrescrever/apagar comando; dono da loja publicando catálogo e marcando resultado; conta da loja com `storeType` diferente de `restaurant`.

## 9. Inicialização e uso do Firebase no site

Exemplo simplificado de inicialização:

```javascript
import { initializeApp } from "firebase/app";
import { getAuth, signOut } from "firebase/auth";
import { getDatabase } from "firebase/database";
import { signInWaiter } from "./waiterAuth";

const app = initializeApp({
  apiKey: import.meta.env.VITE_FIREBASE_API_KEY,
  authDomain: import.meta.env.VITE_FIREBASE_AUTH_DOMAIN,
  databaseURL: import.meta.env.VITE_FIREBASE_DATABASE_URL,
  projectId: import.meta.env.VITE_FIREBASE_PROJECT_ID
});

export const auth = getAuth(app);
export const db = getDatabase(app);

export async function signIn(storeSlug, waiterNumber, password) {
  return signInWaiter(auth, db, storeSlug, waiterNumber, password);
}

export async function signOutWaiter() {
  await signOut(auth);
}
```

Firebase Web API Key e IDs públicos do projeto identificam o projeto, mas não autorizam operações; a autorização vem do usuário autenticado e das regras. Ainda assim, não versionar senhas, tokens de sessão nem credenciais administrativas. Configure domínios autorizados em Firebase Authentication e use HTTPS no site publicado.

Após autenticar:

1. Obtenha `auth.currentUser.uid`.
2. Use o `storeUid` retornado do `waiterDirectory/{uid}` após validar o `storeSlug` e o número do funcionário informados.
3. Confirme `clients/{storeUid}/storeType === "restaurant"` e a concessão ativa em `restaurantAccess/{uid}`. Se o perfil estiver ausente ou divergente, encerre a sessão e negue acesso.
4. Consulte `restaurantLive/menu` para categorias/itens ativos e somente as áreas atribuídas, quando a projeção das áreas estiver disponível.
5. Crie pedidos usando os IDs da projeção, aguarde `status`, e mostre erros de regra/network sem transformar erro em confirmação.
6. Ao sair, cancele listeners e chame `signOut`.

## 10. Desconexões, duplicidade e atualização

- `createdAt` ajuda a exibir/ordenar, mas não é mecanismo de conflito nem relógio confiável.
- `commandId` é a chave de idempotência. Guarde tentativa pendente no navegador e não regenere o ID em retry.
- A inbox SQLite do desktop deve ter índice UNIQUE em `CommandId`.
- O desktop deve aceitar repetição de entrega do Firebase e retornar o mesmo recibo sem repetir baixa de estoque ou inserção do item.
- A outbox existente do desktop continua enviando eventos de saída; ela é separada da inbox de comandos.
- Não use `set()` para substituir `restaurantLive/areas/{areaId}` a partir do site. A projeção é escrita somente pelo desktop.
- Regras Firebase não implementam bloqueio distribuído da mesa. Para pedido de inclusão, o desktop serializa e valida os comandos numa transação SQLite. O site aguarda o resultado.
- Se for preciso permitir edição ou cancelamento por garçom, defina novos comandos explícitos, permissões e validações no documento e nas regras. Não dê escrita direta ao objeto inteiro da comanda.

## 11. Testes antes de publicar

Use um projeto Firebase de desenvolvimento e o Realtime Database Emulator quando possível. Não experimente regras de escrita diretamente na base real da loja.

Checklist mínimo:

1. Sem login: leitura e gravação negadas.
2. Garçom de outra loja: leitura e gravação negadas.
3. Garçom sem concessão ou desativado: negado.
4. Garçom ativo: lê o menu e apenas as áreas atribuídas.
5. Garçom ativo: cria pedido válido com `status=pending`.
6. Garçom não consegue alterar `price`, `stock`, pagamento, `status`, nem apagar ou sobrescrever um comando.
7. Garçom não consegue escolher UID de outro funcionário ou área não atribuída.
8. Proprietário desktop consegue publicar snapshot e atualizar resultado do comando.
9. Duas submissões com IDs diferentes geram duas intenções; o mesmo ID é aplicado no máximo uma vez pelo desktop.
10. Produto/mesa removido, menu inativo, estoque insuficiente ou mesa paga são rejeitados pelo desktop, sem alterar a SQLite nem a projeção.
11. Site offline/reconectado não mostra pedido como confirmado antes do resultado do desktop.
12. Loja alternada para `sales` perde acesso ao caminho `restaurant`.
13. Login com `storeSlug` e número válidos autentica a conta esperada; `0042` não pode ser tratado como `42`, e perfil/diretório divergentes devem encerrar a sessão.
14. O site lê o menu da loja autenticada em `restaurantLive/menu`, agrupa por categorias e não oferece itens inativos para novos pedidos.

Publique as regras usando o procedimento normal do projeto Firebase somente depois desses testes e revise o resultado das regras no console. A publicação das regras não atualiza o SYSMR instalado nem adiciona o consumidor de comandos ao desktop.

## 12. Próximas etapas

1. **Site web:** consumir `openOrder` em cada mesa para mostrar o estado atual da comanda, inclusive itens adicionados no desktop; acompanhar individualmente cada `waiterCommands/{commandId}` até `applied` ou `rejected`.
2. **Regras:** publicar a versão atualizada de `SYSMR.FirebaseFunctions/database.rules.json` e validar gravação pendente pelo garçom, leitura restrita e atualização do recibo pelo desktop no emulador.
3. **Piloto:** testar com uma loja e um desktop conectado. Enviar item pelo webapp e confirmar que aparece no desktop; adicionar pelo desktop e confirmar que aparece no webapp; repetir com queda e retorno de internet.
4. **Multi-terminal:** não habilitar dois desktops SQLite como autoridade da mesma loja até existir sincronização bidirecional e resolução de conflitos.

## 13. Referências ao código atual

- O worker atual envia eventos de saída em ordem da fila local: `SYSMR.Infrastructure/Persistence/FirebaseSyncWorker.cs`.
- A gravação REST do desktop escreve em `clients/{uid}/branches/{branch}/events/{eventId}`: `SYSMR.Infrastructure/Persistence/FirebaseStoreAccountClient.cs`.
- O envelope do evento existente é criado por `SYSMR.Infrastructure/Persistence/SqliteSyncOutboxRepository.cs`.
- Itens de comandas são gravados pelo repositório `SYSMR.Infrastructure/Persistence/SqliteRestaurantRepository.cs`.
- Os IDs do cardápio, áreas e mesas são os GUIDs do SQLite e são publicados por loja. Comandas abertas aparecem no campo `openOrder` da respectiva mesa.
- O worker e cliente REST do desktop processam `waiterCommands`, atualizam os recibos e republicam as comandas. A publicação das regras em `SYSMR.FirebaseFunctions/database.rules.json` ainda deve ser feita no projeto Firebase.
