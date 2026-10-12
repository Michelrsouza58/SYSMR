# SYSMR

SYSMR is an offline-first Windows point-of-sale application foundation using C#/.NET 8 and WPF, organized into Domain, Application, Infrastructure, and UI projects.

## Run and test

Requirements: Windows 10/11 x64 and the .NET 8 SDK (or .NET 8 Desktop Runtime to run a framework-dependent published build).

```powershell
dotnet build SYSMR.sln
dotnet test SYSMR.sln
dotnet run --project SYSMR.UI\SYSMR.UI.csproj
```

The first launch creates `%LOCALAPPDATA%\SYSMR\sysmr.db` and seeds three development products. It asks the machine owner to create the Super Admin PIN (8 or more characters), then displays one login screen with two tabs: **Lojista** for the store account, and **SYSMR** for a local Super Admin PIN session. During account provisioning, the Super Admin selects **Vendas / PDV** or **Restaurante**; the choice is stored in the Firebase profile and local account cache. After the first successful store login, the account session is remembered on this computer and restored automatically on later launches, with a loading screen instead of asking for the store password again. **Sair da conta** turns off automatic login; the next launch will show the login screen. The Super Admin PIN remains separate from the store password and is not remembered as an elevated session.

Account provisioning requires Firebase Authentication with the **Email/Password** provider enabled, a Firebase Web API Key, a Realtime Database URL, and the deployed `provisionStore` Cloud Function. Configure these using **Configurar Firebase** on the login screen (local Super Admin PIN required). The Web API Key is a public Firebase client identifier; never enter service-account JSON, administrator passwords, or tokens. The global Firebase Super Admin credentials are entered only for a provisioning request and are not saved by SYSMR. The Cloud Function allows only the configured global Super Admin UID to create store accounts. Firebase Auth stores the store password; the Realtime Database stores only the store number/name profile. Each store number maps internally to a synthetic Firebase email, so the customer number and password are the user-facing credentials. Creating the store account returns to the login screen; sign in with its number, name, and password to continue into the application. The local credential cache is written only after a successful online login.

After a successful online login, the store credentials are cached in SQLite as a salted PBKDF2 password hash for offline sign-in, and automatic session restoration is enabled. The raw password and Firebase ID tokens are not persisted; the refresh token is protected with Windows DPAPI and can only be opened by the same Windows user. Automatic login means anyone with access to this Windows user account can open the local SYSMR session; use **Sair da conta** on shared terminals. If Firebase cannot be reached after signing out, the last successfully authenticated account can still sign in offline using its password. Invalid online credentials are not accepted through the offline path. Each local database is bound to one store account to prevent accidentally opening another store's local sales and inventory. Keep a secure copy of the Super Admin PIN; reset/recovery has not been implemented. The seller is required in the payment dialog for every sale and is selected from the active registered sellers. Returns are available from the toolbar to the store session and do not require the Super Admin PIN; inventory and cash reversal are still recorded together with the return.

Deploy the included function and database rules. Create the global Super Admin user in Firebase Authentication first, set that user's UID in `SYSMR.FirebaseFunctions/.env`, install the Functions dependencies, and deploy from the repository root:

```powershell
Copy-Item SYSMR.FirebaseFunctions\.env.example SYSMR.FirebaseFunctions\.env
# Edit SYSMR.FirebaseFunctions\.env and replace the UID placeholder.
npm --prefix SYSMR.FirebaseFunctions install
firebase deploy --only functions,database --project YOUR_FIREBASE_PROJECT_ID
```

Then enter the HTTPS function URL (for example, `https://us-central1-YOUR_FIREBASE_PROJECT_ID.cloudfunctions.net/provisionStore`) with the other Firebase settings in SYSMR. Redeploy the function and database rules after pulling changes to account provisioning. The local PIN protects this computer's administrative screen; the server-side UID check is what authorizes account creation globally.

Realtime Database rules keep the provisioned store profile read-only to store clients and allow each
authenticated store to write only to its own branch, which must match the `storeType` in its profile.
Operational records are immutable-ID events under `clients/{uid}/branches/{sales|restaurant}/events`.
The application retries the local outbox every 20 seconds; failed uploads retain an attempt count and
last error in SQLite. The refresh token used by the worker is protected with Windows DPAPI for the
current Windows user. Publish the included rules before expecting cloud writes:

```json
{
  "rules": {
    "clients": {
      "$uid": {
        ".read": "auth != null && auth.uid === $uid",
        "storeType": {
          ".write": "auth != null && auth.uid === $uid",
          ".validate": "newData.val() === 'sales' || newData.val() === 'restaurant'"
        },
        "branches": {
          "$branch": {
            ".read": "auth != null && auth.uid === $uid && root.child('clients').child($uid).child('storeType').val() === $branch",
            "$eventId": {
              ".write": "auth != null && auth.uid === $uid && root.child('clients').child($uid).child('storeType').val() === $branch && ($branch === 'sales' || $branch === 'restaurant')",
              ".validate": "newData.hasChildren(['id', 'entity', 'entityId', 'createdAt', 'payload']) && newData.child('id').val() === $eventId"
            }
          }
        }
      }
    }
  }
}
```

To publish a self-contained Windows x64 folder:

```powershell
dotnet publish SYSMR.UI\SYSMR.UI.csproj -c Release -r win-x64 --self-contained true
```

Distribute `SYSMR.UI\bin\Release\net8.0-windows\win-x64\publish`. Keep the merchant's `%LOCALAPPDATA%\SYSMR\sysmr.db` when upgrading; back it up before replacing an installation.

## Navigation and operating

The toolbar at the top provides **Venda**, **Vendas**, **Devoluções**, **Clientes**, **Estoque**, **Config.**, and **Ajuda**.

- In **Venda**, scan a product in the barcode field (USB HID reader sending Enter), or use F2 to search and double-click a result. F4 focuses the optional document field; F5 opens payment. Select the responsible seller from the list of active sellers before completing the sale.

After a sale or return is committed locally, SYSMR asks whether to print a **non-fiscal receipt**. Confirming opens the Windows print dialog so an installed printer can be selected. The saved Windows printer queue is preselected when available; choose another queue in the dialog if needed. The receipt uses a clean commercial style sized for 80 mm thermal paper, with item description, quantity, unit value, total, attendant, and payment. Item and amount columns have fixed widths to prevent narrow printer drivers from wrapping descriptions one character at a time. It is explicitly marked non-fiscal and does not emit NFC-e or a fiscal document. Thermal ESC/POS direct commands are not integrated; Windows prints using the selected queue and its driver.

In **Vendas**, selecting a sale also asks whether to print its saved non-fiscal receipt. In **Devoluções**, selecting a sale with previous returns asks whether to print a summary of the quantities and amounts already returned. These are optional print actions and do not modify the sale or return records.
- The sale total, customer, payment method, and line items are saved locally. Open **Vendas**, select a row, and inspect its products in the detail table.
- **Devoluções** is available to the store session without the Super Admin PIN. Select a sale, enter a quantity for one or more products, and confirm. The app caps returns at the unsold quantity per product, restores stock, records the proportional refund using the original payment method, and writes the audit record in the same SQLite transaction. A sale can be returned across multiple operations until its items are fully returned.
- **Clientes** is restricted by the Super Admin PIN. Create or edit a customer, select **Renovar 30 dias** after review, and see the expiry status. An expired registration is only an alert: it does not prevent selecting the customer or completing a sale.
- **Estoque** is available to the store session without the Super Admin PIN. The lojista can view, create, and edit products, including their stock quantities. **Configurações** is available to the lojista for seller management, receipt details, and printer selection. **Clientes** and **Auditoria** remain protected by the Super Admin PIN. The initial bootstrap is performed once on first launch.
- In **Configurações**, add and edit sellers, then deactivate or reactivate them. Only active sellers appear in the payment selector; historical sales retain the seller name saved at the time. Store settings also allow customizing the receipt display name, phone, address, footer message, and preferred Windows printer. These settings are local to this installation.
- Use **Entrar como Super Admin** inside **Configurações** to reveal privileged Firebase settings; customers and audit navigation also require the Super Admin PIN. The option to exit the Super Admin session remains available in Configurações and the header.
- In **Estoque**, use **Ver estoque baixo (≤ 2)** to show only products with stock quantity at or below two; **Mostrar todos** restores the full list. Product create/edit includes a unit of measure (UN, KG, L, CX, etc.), stock and minimum-stock fields. F3 opens product creation with local voice dictation; this is available to the store session.
- **F3** opens the product editor and starts Windows-local speech recognition for the selected field. Select a field and press **Iniciar escuta** to dictate further values. The recognized text is shown for review before saving; audio is not stored or sent to a cloud service. This requires a working microphone and the Windows Brazilian Portuguese (`pt-BR`) speech recognition component. If it is missing, install that Windows language/speech feature; the form remains usable by keyboard.
- **Auditoria** lists local action records. Sales and returns are logged in the same database transaction as their stock/cash records; product/customer/configuration actions, operator turn starts, and Super Admin session starts/ends are also recorded.
- Esc requires Super Admin authorization to remove a cart item or cancel the sale.

A 13-digit scale label is recognized in the format `20 + 5-digit scale product code + 5-digit weight in grams + EAN-13 check digit`. Configure a product with its scale code and mark it as sold by weight.

## Restaurant operation

When provisioning a store, the Super Admin can select **Restaurante**. That store opens a restaurant dashboard using the same SYSMR color palette and navigation style, with separate local restaurant data:

- **Salão e mesas:** Areas such as Salão, Varanda, or Área 1. The initial Salão has 12 tables; configure the number per area in settings. Free tables are gray, occupied tables are green, and table history is retained when the configured count is reduced.
- **Comandas:** Select a table to view or resume its order; choosing a table alone does not occupy it. The order is created with the first item in the same transaction, so failed additions do not leave an occupied table. Use the trash icon on an item and confirm to remove it; linked stock is restored, and removing the last item frees the table. Items cannot be removed after a partial payment has been recorded. Assign an active employee, add items from the independent restaurant menu, and record one or more payments by cash, Pix, or card. Use **Usar saldo total** to fill the remaining amount, enter a custom payment amount, or set the number of people and use **Calcular parcela** to fill one equal share (the last share may differ by a cent due to rounding). The account offers an optional 10% waiter service charge; it is fixed when the first payment is registered and cannot change across later partial payments. A partial payment leaves the balance and table open; the table returns to gray only after the balance reaches zero. Menu items can be linked to a stock product by barcode and a per-serving quantity, which is deducted transactionally when an item is added. **Imprimir cozinha** opens the Windows printer selector and prints the current order as a non-fiscal kitchen ticket.
- **Cardápio and Funcionários:** Add and deactivate menu entries and staff. Edit item name, category, and price directly in the menu grid; use the separate edit dialog for stock links. After entering Super Admin in restaurant settings, choose a cardápio template from the dropdown (Pesqueiro, Churrascaria, Pizzaria, Lanchonete / Hamburgueria, or Restaurante caseiro) and confirm to add its sample items. Existing names are not duplicated, and sample prices must be reviewed before selling. Staff records are operational names/roles, not separate authenticated accounts or permission profiles.
- **Caixa:** Open a cash session with its opening float, collect restaurant payments only while a cash session is open, and close it with the counted cash amount. Open tables prevent cash closure.
- **Configurações:** Configure areas and table counts. Use **Entrar como Super Admin** to unlock the store-format control for the current window; the Super Admin PIN is required, and leaving this mode hides the control again. **Sair da conta** is at the bottom of Configurações and disables remembered login without deleting local data.
- **Formato da loja:** In Configurações (after entering Super Admin), use the format toggle to switch between Restaurante and Lojista / PDV. The authenticated store session, deployed database rules and an empty pending queue for the current branch are required. SYSMR closes after a successful switch; reopen it to load the selected module. Data from the previous branch is retained.

Restaurant records are saved offline in the local SQLite database. Order-item, payment, cash-session and operational setup events, plus sales, returns, stock changes and product edits, are placed in a transactional SQLite outbox and uploaded to the authenticated store branch when Firebase is reachable. The worker resumes on application startup; an internet connection alone is not sufficient unless the account has completed an online sign-in and a refresh token is available. On computers with an account remembered from an older SYSMR version, choose **Sair da conta** and sign in online once to save the protected refresh token. Existing local data is not bulk-uploaded automatically; only newly recorded events are queued. The app does not download or merge remote data, so this is outbound event synchronization, not multi-device conflict resolution. Fiscal receipts/NFC-e and employee login/permissions remain out of scope.

## Deployment notes

1. **System:** Windows 10/11 x64. Install .NET 8 Desktop Runtime for a framework-dependent build; a self-contained publish includes the runtime.
2. **Application/data:** run the published executable from a write-accessible location. The database is created under the current user's Local AppData.
3. **Printers:** the Super Admin configuration page enumerates queues installed in Windows, including installed USB, serial and network printers, and can save a default Windows queue. Network thermal IP can also be recorded. ESC/POS receipt printing is not connected yet.
4. **Fiscal:** install A1 in the Windows certificate store or connect the A3 vendor middleware. This version does not sign or emit NFC-e and is not a fiscal contingency implementation.
5. **Firebase:** enable Email/Password under Firebase Authentication, provision the global Super Admin UID for the Cloud Function, deploy the included Function and Realtime Database rules, then configure the Firebase Web API Key, Realtime Database URL, and Function URL in SYSMR. `database.rules.json` must be deployed after updates: event uploads are written to `clients/{uid}/branches/{branch}/events/{eventId}`, and the rules validate this exact path and active store branch. A `Permission denied (401)` from an older deployment may mean the deployed rules still place the event-ID rule directly under `branches/{branch}` instead of under `events/{eventId}`. Never paste service-account JSON or Firebase admin credentials into the app or repository.

## Current boundaries

Working paths include local product search and scan, scale quantity extraction, payment selection, seller-attributed transactional local sales/stock decrement, sale history/detail lookup, once-only full returns with stock and cash-ledger reversal, customer records with 30-day review reminders, protected Super Admin areas, local action auditing, Windows printer queue listing, product create/edit with a saved unit of measure, low-stock filtering at two or fewer units, and local Windows speech dictation into product fields when `pt-BR` speech recognition is installed.

Still not production-ready: authenticated seller accounts and full RBAC (seller attribution currently uses a typed display name), cloud download/merge and multi-device conflict resolution, purchase receiving and full stock adjustment history, cash opening/closing screens for the sales branch, direct ESC/POS printing, NFC-e/certificate integration, period-based dashboard, signed installer, and PIN recovery. Store account login/profile uses Firebase. Voice dictation depends on the Windows pt-BR recognizer and needs real-hardware acceptance testing. The seeded catalog is demo data. Do not treat local payment recording as fiscal issuance.
#   S Y S M R  
 