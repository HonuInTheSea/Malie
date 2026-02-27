import { ChangeDetectionStrategy, Component } from '@angular/core';
import { Ripple } from 'primeng/ripple';
import { ScrollTop } from 'primeng/scrolltop';

@Component({
  selector: 'app-help-page',
  imports: [ScrollTop, Ripple],
  templateUrl: './help-page.component.html',
  styleUrl: './help-page.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class HelpPageComponent {
  private activeRippleTimer: number | null = null;
  private activeRippleFollowupTimer: number | null = null;

  scrollToSection(sectionId: string): void {
    const target = document.getElementById(sectionId);
    if (!target) {
      return;
    }

    target.scrollIntoView({ behavior: 'smooth', block: 'center', inline: 'nearest' });

    if (this.activeRippleTimer !== null) {
      window.clearTimeout(this.activeRippleTimer);
      this.activeRippleTimer = null;
    }

    if (this.activeRippleFollowupTimer !== null) {
      window.clearTimeout(this.activeRippleFollowupTimer);
      this.activeRippleFollowupTimer = null;
    }

    // Initial highlight after smooth-scroll starts.
    this.activeRippleTimer = window.setTimeout(() => {
      this.focusAndHighlight(target);
      this.activeRippleTimer = null;
    }, 360);

    // Follow-up highlight for lower sections where smooth scrolling can settle later.
    this.activeRippleFollowupTimer = window.setTimeout(() => {
      this.focusAndHighlight(target);
      this.activeRippleFollowupTimer = null;
    }, 760);
  }

  private focusAndHighlight(target: HTMLElement): void {
    const rect = target.getBoundingClientRect();
    const isOutsideViewport = rect.bottom < 0 || rect.top > window.innerHeight;
    if (isOutsideViewport) {
      target.scrollIntoView({ behavior: 'smooth', block: 'center', inline: 'nearest' });
    }

    target.setAttribute('tabindex', '-1');
    if (typeof target.focus === 'function') {
      target.focus({ preventScroll: true });
    }

    this.activateSectionRipple(target);
  }

  private activateSectionRipple(target: HTMLElement): void {
    this.activatePrimeRipple(target);
    target.classList.remove('section-ripple-active');
    void target.offsetWidth;
    target.classList.add('section-ripple-active');
    window.setTimeout(() => target.classList.remove('section-ripple-active'), 1450);
  }

  private activatePrimeRipple(target: HTMLElement): void {
    const rect = target.getBoundingClientRect();
    const clientX = rect.left + rect.width / 2;
    const clientY = rect.top + Math.min(rect.height * 0.22, rect.height / 2);
    target.dispatchEvent(
      new MouseEvent('mousedown', {
        bubbles: true,
        cancelable: true,
        clientX,
        clientY,
        buttons: 1
      })
    );
    target.dispatchEvent(
      new MouseEvent('mouseup', {
        bubbles: true,
        cancelable: true,
        clientX,
        clientY,
        buttons: 0
      })
    );
  }
}
