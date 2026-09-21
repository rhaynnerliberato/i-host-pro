import { Component, input } from '@angular/core';

/**
 * UI/UX Visual Foundation gate — the app's one shared brand mark (a "iH"
 * monogram + "iHostPro" wordmark, built as CSS/typography, not a raster/SVG
 * asset — no professional logo exists yet, and this gate must not block on
 * one). Used everywhere the product's identity needs to appear: the admin
 * toolbar, the housekeeper portal toolbar, and the auth pages' brand panel.
 */
@Component({
  selector: 'app-brand-mark',
  imports: [],
  templateUrl: './brand-mark.html',
  styleUrl: './brand-mark.scss',
})
export class BrandMark {
  /** Renders only the monogram badge, without the "iHostPro" wordmark — for tight spaces (collapsed nav, favicon-like contexts). */
  readonly compact = input(false);

  /** Inverts the monogram badge to read on a dark/colored surface instead of the default surface-tinted treatment. */
  readonly inverse = input(false);
}
