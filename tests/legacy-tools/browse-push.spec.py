"""
Playwright test to diagnose the push-to-device modal issue on the BrowseVault page.
Tests:
1. Browse page loads and shows vault cards
2. Push button is visible on local vault cards
3. Clicking push button opens the modal
4. Device checkboxes can be checked
5. Push button in modal is enabled after selecting a device
6. Clicking push button triggers the API call
"""

import asyncio
import json
from playwright.async_api import async_playwright

WEBUI_BASE = "http://127.0.0.1:5177"
FAMILY_BASE = "http://127.0.0.1:8788"


async def get_cli_token():
    """Get CLI token for authentication."""
    from urllib.request import Request, urlopen
    req = Request(f"{WEBUI_BASE}/api/auth/cli-token", method="POST", data=b"")
    with urlopen(req) as resp:
        data = json.loads(resp.read())
        return data.get("token", "")


async def main():
    async with async_playwright() as p:
        browser = await p.chromium.launch(headless=True)
        context = await browser.new_context(viewport={"width": 1280, "height": 900})
        page = await context.new_page()

        # Collect console logs and network errors
        console_logs = []
        network_errors = []
        api_calls = []

        page.on("console", lambda msg: console_logs.append(f"[{msg.type}] {msg.text}"))
        page.on("requestfailed", lambda req: network_errors.append(f"{req.method} {req.url} - {req.failure}"))
        page.on("request", lambda req: api_calls.append(f"{req.method} {req.url}") if "/api/" in req.url else None)

        # Step 1: Get CLI token and navigate to browse page
        print("\n=== Step 1: Navigate to browse page ===")
        token = await get_cli_token()
        print(f"CLI token obtained: {token[:20]}...")

        await page.goto(f"{WEBUI_BASE}/browse?cli-token={token}", wait_until="networkidle", timeout=30000)
        await page.wait_for_timeout(3000)  # Wait for Blazor to render

        # Take screenshot
        await page.screenshot(path="/tmp/browse-step1-loaded.png")
        print("Screenshot saved: /tmp/browse-step1-loaded.png")

        # Step 2: Check vault cards
        print("\n=== Step 2: Check vault cards ===")
        vault_cards = page.locator(".vault-folder-card")
        card_count = await vault_cards.count()
        print(f"Found {card_count} vault cards")

        if card_count == 0:
            print("ERROR: No vault cards found! Checking page content...")
            body_text = await page.locator("body").inner_text()
            print(f"Page body text (first 500 chars): {body_text[:500]}")
            await browser.close()
            return

        # Check for source badges
        local_badges = page.locator("text=本地")
        mobile_badges = page.locator("text=移动端推送")
        local_count = await local_badges.count()
        mobile_count = await mobile_badges.count()
        print(f"Local badges: {local_count}, Mobile badges: {mobile_count}")

        # Step 3: Find push buttons
        print("\n=== Step 3: Find push buttons ===")
        push_buttons = page.locator("button", has_text="推送到设备")
        push_btn_count = await push_buttons.count()
        print(f"Found {push_btn_count} '推送到设备' buttons")

        if push_btn_count == 0:
            print("ERROR: No push buttons found! Checking card structure...")
            first_card = vault_cards.first
            card_html = await first_card.inner_html()
            print(f"First card HTML (first 500 chars): {card_html[:500]}")
            await browser.close()
            return

        # Step 4: Click the first push button
        print("\n=== Step 4: Click push button ===")
        first_push_btn = push_buttons.first
        is_visible = await first_push_btn.is_visible()
        is_enabled = await first_push_btn.is_enabled()
        print(f"Push button visible: {is_visible}, enabled: {is_enabled}")

        if not is_visible or not is_enabled:
            print("ERROR: Push button not visible or not enabled!")
            await browser.close()
            return

        await first_push_btn.click()
        await page.wait_for_timeout(3000)  # Wait for modal and device list to load

        await page.screenshot(path="/tmp/browse-step4-modal.png")
        print("Screenshot saved: /tmp/browse-step4-modal.png")

        # Step 5: Check modal content
        print("\n=== Step 5: Check modal content ===")
        modal = page.locator(".modal.show, .modal[style*='display:block']")
        modal_visible = await modal.count() > 0
        print(f"Modal visible: {modal_visible}")

        if not modal_visible:
            print("ERROR: Modal not visible after clicking push button!")
            print("Checking for any modal elements...")
            all_modals = page.locator(".modal")
            print(f"Total .modal elements: {await all_modals.count()}")
            for i in range(await all_modals.count()):
                m = all_modals.nth(i)
                style = await m.get_attribute("style")
                print(f"  Modal {i} style: {style}")
            await browser.close()
            return

        # Check for device list
        checkboxes = page.locator(".modal .form-check-input")
        checkbox_count = await checkboxes.count()
        print(f"Found {checkbox_count} device checkboxes in modal")

        if checkbox_count == 0:
            # Check if there's a "no devices" message
            no_devices = page.locator("text=暂无已授权设备")
            if await no_devices.count() > 0:
                print("INFO: No authorized devices found - this is expected if no devices are paired")
            else:
                print("WARNING: No checkboxes and no 'no devices' message")
                modal_html = await modal.first.inner_html()
                print(f"Modal HTML (first 500 chars): {modal_html[:500]}")
            await browser.close()
            return

        # Step 6: Check a device checkbox
        print("\n=== Step 6: Check device checkbox ===")
        first_checkbox = checkboxes.first
        is_checked_before = await first_checkbox.is_checked()
        print(f"Checkbox checked before click: {is_checked_before}")

        await first_checkbox.check()
        await page.wait_for_timeout(500)

        is_checked_after = await first_checkbox.is_checked()
        print(f"Checkbox checked after click: {is_checked_after}")

        if not is_checked_after:
            print("ERROR: Checkbox did not become checked! This is the bug.")
            # Try clicking instead
            print("Trying click() instead of check()...")
            await first_checkbox.click()
            await page.wait_for_timeout(500)
            is_checked_after_retry = await first_checkbox.is_checked()
            print(f"Checkbox checked after click(): {is_checked_after_retry}")

        await page.screenshot(path="/tmp/browse-step6-checkbox.png")
        print("Screenshot saved: /tmp/browse-step6-checkbox.png")

        # Step 7: Check push button state in modal
        print("\n=== Step 7: Check push button in modal ===")
        modal_push_btn = page.locator(".modal button", has_text="推送")
        modal_push_count = await modal_push_btn.count()
        print(f"Found {modal_push_count} push buttons in modal")

        if modal_push_count > 0:
            btn = modal_push_btn.first
            btn_text = await btn.inner_text()
            btn_enabled = await btn.is_enabled()
            btn_disabled = await btn.get_attribute("disabled")
            print(f"Push button text: '{btn_text}'")
            print(f"Push button enabled: {btn_enabled}")
            print(f"Push button disabled attr: {btn_disabled}")

            # Step 8: Click the push button
            if btn_enabled:
                print("\n=== Step 8: Click push button in modal ===")
                # Monitor API calls
                api_calls_before = len(api_calls)
                await btn.click()
                await page.wait_for_timeout(5000)  # Wait for API call and modal close

                new_api_calls = api_calls[api_calls_before:]
                print(f"New API calls after push click: {new_api_calls}")

                await page.screenshot(path="/tmp/browse-step8-after-push.png")
                print("Screenshot saved: /tmp/browse-step8-after-push.png")

                # Check for success message
                success_msg = page.locator(".alert-success")
                if await success_msg.count() > 0:
                    msg_text = await success_msg.first.inner_text()
                    print(f"Success message: {msg_text}")
                else:
                    warning_msg = page.locator(".alert-warning")
                    if await warning_msg.count() > 0:
                        msg_text = await warning_msg.first.inner_text()
                        print(f"Warning message: {msg_text}")
                    else:
                        print("No feedback message shown after push!")

                # Check if modal closed
                modal_still_visible = await page.locator(".modal[style*='display:block']").count() > 0
                print(f"Modal still visible after push: {modal_still_visible}")
            else:
                print("Push button is disabled - cannot click")
        else:
            print("No push button found in modal")

        # Print collected diagnostics
        print("\n=== Diagnostics Summary ===")
        print(f"Console logs ({len(console_logs)}):")
        for log in console_logs[-20:]:
            print(f"  {log}")
        print(f"\nNetwork errors ({len(network_errors)}):")
        for err in network_errors:
            print(f"  {err}")
        print(f"\nAPI calls ({len(api_calls)}):")
        for call in api_calls:
            print(f"  {call}")

        await browser.close()


if __name__ == "__main__":
    asyncio.run(main())
