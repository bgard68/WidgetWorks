<#
.SYNOPSIS
    End-to-end smoke test for the WidgetWorks API.

.DESCRIPTION
    Exercises the running API across catalog, auth (register/login/refresh/logout),
    2FA (real TOTP enroll -> confirm -> challenge), cart, checkout (mock payment
    success + decline), admin catalog + order-status transitions, guest order
    lookup, Google sign-in with a fake credential, and a battery of failure
    conditions (401 / 403 / 404 / 400). Prints PASS/FAIL per check and exits
    non-zero if anything failed.

.PARAMETER BaseUrl
    Base URL of the API. Default http://localhost:8080 (the docker compose port).

.EXAMPLE
    pwsh ./scripts/smoke-test.ps1
    powershell -File .\scripts\smoke-test.ps1 -BaseUrl http://localhost:8080

.NOTES
    Works on Windows PowerShell 5.1 and PowerShell 7+. The API must be running
    (docker compose up --build). The test creates throwaway users/widgets/orders
    in the dev database; that is expected.
#>
[CmdletBinding()]
param(
    [string]$BaseUrl = 'http://localhost:8080',
    [string]$AdminEmail = 'admin@widgetworks.demo',
    [string]$AdminPassword = 'DemoAdmin!Change01',
    [switch]$SkipTwoFactor
)

$ErrorActionPreference = 'Stop'
$BaseUrl = $BaseUrl.TrimEnd('/')

# ---------------------------------------------------------------------------
# Test harness
# ---------------------------------------------------------------------------
$script:Pass = 0
$script:Fail = 0
$script:Failures = @()

function Section($title) {
    Write-Host ''
    Write-Host "== $title ==" -ForegroundColor Cyan
}

function Check([string]$name, [bool]$ok, [string]$detail = '') {
    if ($ok) {
        $script:Pass++
        Write-Host "  [PASS] $name" -ForegroundColor Green
    }
    else {
        $script:Fail++
        $script:Failures += $name
        if ($detail) { Write-Host "  [FAIL] $name -> $detail" -ForegroundColor Red }
        else { Write-Host "  [FAIL] $name" -ForegroundColor Red }
    }
}

function New-Email {
    return ('smoke_' + [guid]::NewGuid().ToString('N').Substring(0, 12) + '@smoke.test')
}

# Cross-version HTTP wrapper: never throws on non-2xx; returns Status + parsed Body.
function Invoke-Api {
    param(
        [string]$Method = 'GET',
        [Parameter(Mandatory = $true)][string]$Path,
        $Body = $null,
        [string]$Token = $null
    )
    $headers = @{ 'Accept' = 'application/json' }
    if ($Token) { $headers['Authorization'] = "Bearer $Token" }

    $params = @{
        Method          = $Method
        Uri             = "$BaseUrl$Path"
        Headers         = $headers
        UseBasicParsing = $true
        TimeoutSec      = 30
    }
    if ($null -ne $Body) {
        $params['Body'] = ($Body | ConvertTo-Json -Depth 8 -Compress)
        $params['ContentType'] = 'application/json'
    }

    $status = 0
    $content = ''
    if ($PSVersionTable.PSVersion.Major -ge 7) {
        $params['SkipHttpErrorCheck'] = $true
        $resp = Invoke-WebRequest @params
        $status = [int]$resp.StatusCode
        $content = [string]$resp.Content
    }
    else {
        try {
            $resp = Invoke-WebRequest @params
            $status = [int]$resp.StatusCode
            $content = [string]$resp.Content
        }
        catch {
            $ex = $_.Exception
            if ($ex.Response) {
                $status = [int]$ex.Response.StatusCode
                try {
                    $reader = New-Object System.IO.StreamReader($ex.Response.GetResponseStream())
                    $content = $reader.ReadToEnd()
                }
                catch { $content = '' }
            }
            else { $status = 0; $content = $ex.Message }
        }
    }

    $obj = $null
    if ($content) { try { $obj = $content | ConvertFrom-Json } catch { $obj = $content } }
    return [pscustomobject]@{ Status = $status; Body = $obj; Raw = $content }
}

# ---------------------------------------------------------------------------
# RFC 6238 TOTP (SHA-1, 30s, 6 digits) for the real 2FA flow
# ---------------------------------------------------------------------------
function Convert-FromBase32([string]$b32) {
    $b32 = $b32.TrimEnd('=').ToUpperInvariant()
    $alphabet = 'ABCDEFGHIJKLMNOPQRSTUVWXYZ234567'
    $bits = ''
    foreach ($c in $b32.ToCharArray()) {
        $i = $alphabet.IndexOf($c)
        if ($i -lt 0) { continue }
        $bits += [Convert]::ToString($i, 2).PadLeft(5, '0')
    }
    $bytes = New-Object System.Collections.Generic.List[byte]
    for ($i = 0; ($i + 8) -le $bits.Length; $i += 8) {
        $bytes.Add([Convert]::ToByte($bits.Substring($i, 8), 2))
    }
    return , $bytes.ToArray()
}

function Get-Totp([string]$secretBase32, [int]$digits = 6, [int]$period = 30) {
    $key = Convert-FromBase32 $secretBase32
    $counter = [int64][math]::Floor([DateTimeOffset]::UtcNow.ToUnixTimeSeconds() / $period)
    $msg = [BitConverter]::GetBytes($counter)
    if ([BitConverter]::IsLittleEndian) { [Array]::Reverse($msg) }
    $hmac = New-Object System.Security.Cryptography.HMACSHA1
    $hmac.Key = $key
    $hash = $hmac.ComputeHash($msg)
    $offset = $hash[$hash.Length - 1] -band 0x0f
    $bin = ((($hash[$offset] -band 0x7f) -shl 24) -bor `
        (($hash[$offset + 1] -band 0xff) -shl 16) -bor `
        (($hash[$offset + 2] -band 0xff) -shl 8) -bor `
        ($hash[$offset + 3] -band 0xff))
    $otp = $bin % [int][math]::Pow(10, $digits)
    return ([string]$otp).PadLeft($digits, '0')
}

# ===========================================================================
Write-Host "WidgetWorks API smoke test" -ForegroundColor White
Write-Host "Target: $BaseUrl"
Write-Host "PowerShell: $($PSVersionTable.PSVersion)"

# --- Health & catalog -------------------------------------------------------
Section 'Health & catalog'
$h = Invoke-Api GET '/health'
Check 'GET /health returns 200' ($h.Status -eq 200) "status=$($h.Status)"
Check 'health reports ok' ($h.Body.status -eq 'ok')

$list = Invoke-Api GET '/catalog/widgets'
Check 'GET /catalog/widgets returns 200' ($list.Status -eq 200) "status=$($list.Status)"
$hasItems = ($list.Body -and $list.Body.items -and $list.Body.items.Count -ge 1)
Check 'catalog has at least one widget' $hasItems

$widget = $null
if ($hasItems) { $widget = $list.Body.items | Where-Object { $_.quantityAvailable -gt 0 } | Select-Object -First 1 }
if (-not $widget -and $hasItems) { $widget = $list.Body.items[0] }
Check 'found an in-stock widget for cart tests' ($null -ne $widget)

if ($widget) {
    $one = Invoke-Api GET "/catalog/widgets/$($widget.id)"
    Check 'GET /catalog/widgets/{id} returns 200' ($one.Status -eq 200)
    $search = Invoke-Api GET '/catalog/widgets?search=widget'
    Check 'search returns 200' ($search.Status -eq 200)
}

$ship = Invoke-Api GET '/checkout/shipping-methods'
Check 'GET /checkout/shipping-methods returns 200' ($ship.Status -eq 200)
$tax = Invoke-Api GET '/checkout/tax-info'
Check 'GET /checkout/tax-info returns 200' ($tax.Status -eq 200)

# --- Auth: register / login / refresh / logout ------------------------------
Section 'Auth: register, login, refresh, logout'
$custEmail = New-Email
$custPw = 'Customer!Pass123'
$reg = Invoke-Api POST '/auth/register' @{ email = $custEmail; password = $custPw }
Check 'register new customer returns 200' ($reg.Status -eq 200) "status=$($reg.Status)"

$login = Invoke-Api POST '/auth/login' @{ email = $custEmail; password = $custPw }
Check 'login returns 200 with tokens' ($login.Status -eq 200 -and $login.Body.accessToken) "status=$($login.Status)"
$custToken = $login.Body.accessToken
$custRefresh = $login.Body.refreshToken
Check 'login role is Customer' ($login.Body.role -eq 'Customer')

$myOrders = Invoke-Api GET '/orders' $null $custToken
Check 'GET /orders with token returns 200' ($myOrders.Status -eq 200) "status=$($myOrders.Status)"

$refresh = Invoke-Api POST '/auth/refresh' @{ refreshToken = $custRefresh }
Check 'refresh returns 200 with new tokens' ($refresh.Status -eq 200 -and $refresh.Body.accessToken)
if ($refresh.Body.refreshToken) { $custRefresh = $refresh.Body.refreshToken }

$logout = Invoke-Api POST '/auth/logout' @{ refreshToken = $custRefresh }
Check 'logout returns 204' ($logout.Status -eq 204)

# --- Google sign-in with a fake credential ----------------------------------
Section 'Google sign-in (fake credential must be rejected)'
$g = Invoke-Api POST '/auth/google' @{ idToken = 'fake.google.id-token.value' }
Check 'POST /auth/google with fake token returns 401' ($g.Status -eq 401) "status=$($g.Status)"

# --- 2FA: real TOTP enroll -> confirm -> challenge --------------------------
if (-not $SkipTwoFactor) {
    Section '2FA: enroll, confirm (TOTP), and challenge login'
    try {
        $tfaEmail = New-Email
        $tfaPw = 'TwoFactor!Pass123'
        [void](Invoke-Api POST '/auth/register' @{ email = $tfaEmail; password = $tfaPw })
        $tl = Invoke-Api POST '/auth/login' @{ email = $tfaEmail; password = $tfaPw }
        $tfaToken = $tl.Body.accessToken

        $enroll = Invoke-Api POST '/2fa/enroll' $null $tfaToken
        Check 'enroll returns 200 with a secret' ($enroll.Status -eq 200 -and $enroll.Body.secretBase32)
        $secret = $enroll.Body.secretBase32

        if ($secret) {
            $code = Get-Totp $secret
            $confirm = Invoke-Api POST '/2fa/enroll/confirm' @{ code = $code } $tfaToken
            Check 'confirm enroll returns 200 with recovery codes' ($confirm.Status -eq 200 -and $confirm.Body.recoveryCodes.Count -ge 1) "status=$($confirm.Status)"
            $recoveryCodes = $confirm.Body.recoveryCodes

            # Enabling 2FA rotated the security stamp; re-login now requires 2FA.
            $login2 = Invoke-Api POST '/auth/login' @{ email = $tfaEmail; password = $tfaPw }
            Check 'login now requires 2FA (challenge issued)' ($login2.Body.twoFactorRequired -eq $true -and $login2.Body.challengeToken)

            $code2 = Get-Totp $secret
            $verify = Invoke-Api POST '/auth/2fa' @{ challengeToken = $login2.Body.challengeToken; code = $code2 }
            Check 'POST /auth/2fa with TOTP returns tokens' ($verify.Status -eq 200 -and $verify.Body.accessToken) "status=$($verify.Status)"

            $badCode = Invoke-Api POST '/auth/2fa' @{ challengeToken = $login2.Body.challengeToken; code = '000000' }
            Check '2FA with wrong code is rejected' ($badCode.Status -eq 401)

            if ($recoveryCodes -and $recoveryCodes.Count -ge 1) {
                $login3 = Invoke-Api POST '/auth/login' @{ email = $tfaEmail; password = $tfaPw }
                $rec = Invoke-Api POST '/auth/2fa/recovery' @{ challengeToken = $login3.Body.challengeToken; recoveryCode = $recoveryCodes[0] }
                Check 'recovery code logs in' ($rec.Status -eq 200 -and $rec.Body.accessToken)
            }
        }
    }
    catch {
        Check '2FA flow completed without error' $false $_.Exception.Message
    }
}

# --- Admin: login + catalog management --------------------------------------
Section 'Admin: login and catalog management'
$adminLogin = Invoke-Api POST '/auth/login' @{ email = $AdminEmail; password = $AdminPassword }
$adminToken = $adminLogin.Body.accessToken
Check 'admin login returns 200 with tokens' ($adminLogin.Status -eq 200 -and $adminToken) "status=$($adminLogin.Status)"
Check 'admin role is Administrator' ($adminLogin.Body.role -eq 'Administrator')

$newSku = 'SMOKE-' + [guid]::NewGuid().ToString('N').Substring(0, 6).ToUpperInvariant()
$create = Invoke-Api POST '/admin/catalog/widgets' @{ sku = $newSku; name = 'Smoke Test Widget'; description = 'Created by the smoke test'; imageUrl = $null; price = 12.34; quantityOnHand = 25 } $adminToken
Check 'admin create widget returns 201' ($create.Status -eq 201 -and $create.Body.id) "status=$($create.Status)"
$newWidgetId = $create.Body.id

if ($newWidgetId) {
    $getW = Invoke-Api GET "/catalog/widgets/$newWidgetId"
    Check 'created widget is retrievable' ($getW.Status -eq 200 -and $getW.Body.sku -eq $newSku)

    $inv = Invoke-Api POST "/admin/catalog/widgets/$newWidgetId/inventory" @{ quantityOnHandDelta = 5 } $adminToken
    Check 'inventory adjust returns 200' ($inv.Status -eq 200)

    $upd = Invoke-Api PUT "/admin/catalog/widgets/$newWidgetId" @{ name = 'Smoke Test Widget (edited)'; description = 'edited'; imageUrl = $null; price = 13.50; isActive = $true } $adminToken
    Check 'admin update widget returns 204' ($upd.Status -eq 204) "status=$($upd.Status)"

    $adminList = Invoke-Api GET '/admin/catalog/widgets?pageSize=100' $null $adminToken
    Check 'admin list widgets returns 200' ($adminList.Status -eq 200)
}

# --- Cart -> quote -> checkout (success) -> fulfillment ---------------------
Section 'Cart, quote, checkout (mock success), and fulfillment'
$orderNumber = $null
$orderEmail = New-Email
if ($widget) {
    $addItem = Invoke-Api POST '/cart/items' @{ cartId = $null; widgetId = $widget.id; quantity = 2 }
    Check 'add to cart returns 200' ($addItem.Status -eq 200 -and $addItem.Body.id)
    $cartId = $addItem.Body.id

    if ($cartId) {
        $quote = Invoke-Api POST '/checkout/quote' @{ cartId = $cartId; stateCode = 'CA'; shippingMethod = 'Standard' }
        Check 'quote returns 200 with a total' ($quote.Status -eq 200 -and $quote.Body.total -gt 0)

        $checkout = Invoke-Api POST '/checkout' @{
            cartId = $cartId; email = $orderEmail; name = 'Smoke Tester'; line1 = '1 Main St'; line2 = $null
            city = 'Springfield'; state = 'CA'; postalCode = '90001'; country = 'US'
            shippingMethod = 'Standard'; paymentToken = 'tok_visa_ok'
        }
        Check 'checkout (mock success) returns 200 Paid' ($checkout.Status -eq 200 -and $checkout.Body.status -eq 'Paid') "status=$($checkout.Status)"
        $orderNumber = $checkout.Body.orderNumber
        $orderId = $checkout.Body.orderId

        if ($orderId) {
            $adminOrder = Invoke-Api GET "/admin/orders/$orderId" $null $adminToken
            Check 'admin can view the order' ($adminOrder.Status -eq 200)
            $shipped = Invoke-Api POST "/admin/orders/$orderId/status" @{ status = 'Shipped'; trackingNumber = '1Z-SMOKE-123' } $adminToken
            Check 'order Paid -> Shipped returns 200' ($shipped.Status -eq 200 -and $shipped.Body.status -eq 'Shipped')
            $delivered = Invoke-Api POST "/admin/orders/$orderId/status" @{ status = 'Delivered'; trackingNumber = $null } $adminToken
            Check 'order Shipped -> Delivered returns 200' ($delivered.Status -eq 200 -and $delivered.Body.status -eq 'Delivered')
            $badTransition = Invoke-Api POST "/admin/orders/$orderId/status" @{ status = 'Shipped'; trackingNumber = $null } $adminToken
            Check 'illegal transition (Delivered -> Shipped) is rejected' ($badTransition.Status -eq 400)
        }
    }
}

if ($orderNumber) {
    $lookup = Invoke-Api GET ("/orders/lookup?number=$([uri]::EscapeDataString($orderNumber))&email=$([uri]::EscapeDataString($orderEmail))")
    Check 'guest order lookup returns 200' ($lookup.Status -eq 200 -and $lookup.Body.orderNumber -eq $orderNumber)
}

# --- Failure conditions -----------------------------------------------------
Section 'Failure conditions'
$notFound = Invoke-Api GET "/catalog/widgets/$([guid]::NewGuid())"
Check 'GET unknown widget returns 404' ($notFound.Status -eq 404)

$badLogin = Invoke-Api POST '/auth/login' @{ email = $AdminEmail; password = 'definitely-wrong' }
Check 'login with wrong password returns 401' ($badLogin.Status -eq 401)

$noToken = Invoke-Api GET '/orders'
Check 'GET /orders without token returns 401' ($noToken.Status -eq 401)

# a fresh customer token for the 403 check
$c2 = New-Email
[void](Invoke-Api POST '/auth/register' @{ email = $c2; password = 'Customer!Pass123' })
$c2login = Invoke-Api POST '/auth/login' @{ email = $c2; password = 'Customer!Pass123' }
$forbidden = Invoke-Api GET '/admin/catalog/widgets' $null $c2login.Body.accessToken
Check 'customer hitting admin endpoint returns 403' ($forbidden.Status -eq 403) "status=$($forbidden.Status)"

$dupe = Invoke-Api POST '/auth/register' @{ email = $c2; password = 'Customer!Pass123' }
Check 'duplicate registration returns 400' ($dupe.Status -eq 400)

$badReset = Invoke-Api POST '/auth/reset-password' @{ token = 'not-a-real-token'; newPassword = 'brandNewPass1' }
Check 'reset with invalid token returns 400' ($badReset.Status -eq 400)

$forgot = Invoke-Api POST '/auth/forgot-password' @{ email = 'nobody-here@smoke.test' }
Check 'forgot-password is always 200 (no enumeration)' ($forgot.Status -eq 200)

if ($widget) {
    $declineCart = Invoke-Api POST '/cart/items' @{ cartId = $null; widgetId = $widget.id; quantity = 1 }
    if ($declineCart.Body.id) {
        $declined = Invoke-Api POST '/checkout' @{
            cartId = $declineCart.Body.id; email = (New-Email); name = 'Decline Tester'; line1 = '1 Main St'; line2 = $null
            city = 'Springfield'; state = 'CA'; postalCode = '90001'; country = 'US'
            shippingMethod = 'Standard'; paymentToken = 'card-decline'
        }
        Check 'checkout with a declining token returns 400' ($declined.Status -eq 400) "status=$($declined.Status)"
    }
}

# ---------------------------------------------------------------------------
Section 'Summary'
$total = $script:Pass + $script:Fail
Write-Host ''
Write-Host "  Passed: $($script:Pass) / $total" -ForegroundColor Green
if ($script:Fail -gt 0) {
    Write-Host "  Failed: $($script:Fail)" -ForegroundColor Red
    foreach ($f in $script:Failures) { Write-Host "    - $f" -ForegroundColor Red }
    exit 1
}
Write-Host '  All checks passed.' -ForegroundColor Green
exit 0
